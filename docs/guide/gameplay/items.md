---
title: Items
slug: gameplay/items
kind: guide
area: Gameplay
summary: A definition and a sixteen-byte instance, with every affix and every stat recomputed from a roll seed rather than stored.
api: [T:Vixen.Gameplay.Items.ItemDefinition, T:Vixen.Gameplay.Items.ItemInstance, T:Vixen.Gameplay.Items.ItemBinding, T:Vixen.Gameplay.Items.ItemStatDefinition, T:Vixen.Gameplay.Items.ItemRarityDefinition, T:Vixen.Gameplay.Items.AffixDefinition, T:Vixen.Gameplay.Items.AffixPoolDefinition, T:Vixen.Gameplay.Items.AffixStat, T:Vixen.Gameplay.Items.RolledAffix, T:Vixen.Gameplay.Items.ItemTemplate, T:Vixen.Gameplay.Items.AffixTemplate, T:Vixen.Gameplay.Items.ItemRarityTemplate, T:Vixen.Gameplay.Items.ItemLibrary, T:Vixen.Gameplay.Items.ItemAffixes, T:Vixen.Gameplay.Items.ItemStats, T:Vixen.Gameplay.Items.ItemsModule]
tags: [gameplay, items, affixes, rpg, mmo]
since: 0.1
status: preview
related: [gameplay/inventory, gameplay/loot, gameplay/definitions]
---

## What it is

An **item definition** is authored content: a name, a rarity, a slot, an item level, a stack size, a
durability, sockets, a binding policy, tags, stats and the affix pools it rolls from. An **item
instance** is one copy of it, and it is **sixteen bytes**: the definition, a roll seed, a stack count,
a durability and a bound state.

Everything else — which affixes it rolled, what they gave, what the tooltip says — is recomputed from
those sixteen bytes.

## What it is for

A bank of ten thousand items is a real number, and doc 28 § Items is explicit about the consequence:
an instance carrying a materialised stat block is fifty times the memory for data that is a pure
function of the seed.

It buys more than memory. Because the affixes are a function of `(definition, seed)`, the wire carries
a definition id and a seed, and a client's tooltip, a trade window and a realm's damage calculation
agree without any of them being sent — or able to be lied to about — a stat block.

## Using it

Author the item, its rarity and its affix pools as `.vxdef` content; compile the catalog into an
`ItemLibrary` once at load. `ItemTemplate.Create` makes a fresh instance; `ItemAffixes.Roll` recovers
what a seed rolled; `ItemStats.Compute` produces the equip-time block as ordinary `Modifier`s, so an
equipped sword is exactly as much a modifier source as a buff and comes off just as exactly.

⚠ **The affix pool's order is part of what a seed means.** It is sorted by address rather than by the
order a designer listed it, so tidying a YAML file does not re-roll every sword in the game. Adding
an affix to a pool *does* re-roll them, and there is no way around that — it is a content decision
with a visible consequence.

⚠ **Rarity is a definition, not an enum and not a tag.** An enum fixes every game to one ladder; a tag
sorts alphabetically, so a bag sorted by rarity would put Common above Legendary. A rarity needs a
number. It carries a tag as well, so *every legendary drops a token* stays a tag query.

⚠ **Broken is zero durability; indestructible is zero *maximum* durability.** Two fields one word
apart, and reading them the wrong way round makes every stack of ore in the game broken.

## Examples

Authoring an item, near enough doc 28's own example:

```yaml
# Assets/Items/flamebrand.vxitem
!ItemDefinition
displayName: Flamebrand
rarity: rarities/legendary
slot: Item.Slot.MainHand
itemLevel: 80
maximumDurability: 100
sockets: 2
binding: OnEquip
tags: [ Item.Weapon.Sword, Item.Source.Raid ]
stats:
  - { attribute: Power, op: Add, value: 251 }
  - { attribute: Precision, op: Add, value: 179 }
affixPools: [ affixes/pools/weapon ]
icon: icons/flamebrand
prefab: prefabs/weapons/flamebrand
```

Making one, and reading back what its seed rolled:

```csharp compile
using System.Collections.Generic;
using Vixen.Gameplay;
using Vixen.Gameplay.Items;

static class Drops {
    public static ItemInstance Flamebrand(ItemLibrary items, uint seed) =>
        items.Get(DefId.From("items/flamebrand")).Create(stack: 1, seed: seed);

    public static IEnumerable<string> Affixes(ItemLibrary items, ItemInstance instance) {
        var template = items.Get(instance.Definition);

        foreach (var rolled in ItemAffixes.Roll(template, instance.Seed)) {
            yield return items.FindAffix(rolled.Affix)!.Definition.DisplayName;
        }
    }
}
```

Equipping one, which is just a set of modifiers:

```csharp compile
using System.Collections.Generic;
using Vixen.Gameplay;
using Vixen.Gameplay.Items;

static class Equipping {
    public static void Equip(ItemLibrary items, AttributeSet stats, in ItemInstance item, ModifierSource slot) {
        var modifiers = new List<Modifier>();

        ItemStats.Compute(items, item, slot, modifiers);

        foreach (var modifier in modifiers) {
            stats.Add(modifier);
        }
    }

    // Exact, because the kernel removes by source rather than by subtracting values back off.
    public static void Unequip(AttributeSet stats, ModifierSource slot) => stats.RemoveBySource(slot);
}
```

## See also

- [Inventory](gameplay/inventory) — what holds an item, and the only thing that may change one.
- [Loot](gameplay/loot) — where instances come from, and where their seeds come from.
- [Attributes](gameplay/attributes) — what a stat block is made of.
