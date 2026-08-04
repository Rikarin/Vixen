# Vixen.Gameplay.Items

A definition and an instance, and the instance is **sixteen bytes**. Everything an item is — its
affixes, its stat block, its tooltip — is recomputed from those sixteen bytes when somebody looks.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Items, the first half of **G1**.

## State

**Built: definitions, instances, rarity, durability, binding, affix rolling and the equip-time stat
block. 27 tests.** What is not here is anything that *holds* an item — that is
`Vixen.Gameplay.Inventory`, and the split is deliberate: an item does nothing on its own and
everything that happens to one happens inside a container.

| | |
|---|---|
| `ItemDefinition` | What a designer wrote: display name, rarity, slot, item level, stack, durability, sockets, binding, tags, stats, affix pools, icon, prefab. |
| `ItemRarityDefinition` | A tier: a name, an order, an affix count and a tag. ⚠ A definition rather than an enum — see below. |
| `AffixDefinition` · `AffixPoolDefinition` | A rollable modifier with per-stat ranges and its own eligibility, and the shared set an item rolls from. |
| `ItemStatDefinition` | One stat, with a range when `Maximum` is above `Value`. |
| `ItemInstance` | The sixteen bytes: definition, seed, stack, durability, binding. |
| `RolledAffix` | `(affixDefId, roll)` — regenerated, never stored. |
| `ItemTemplate` · `AffixTemplate` · `ItemRarityTemplate` | The compiled forms, with names resolved to tags and `AttributeId`s. |
| `ItemLibrary` | Every item a build knows, compiled once from a `DefinitionCatalog`, with a `Problems` list rather than a throw. |
| `ItemAffixes` | Seed → affixes. Weighted, without replacement, filtered by the item's level and tags. |
| `ItemStats` | The block computed on equip, as ordinary `Modifier`s. |
| `ItemsModule` | Four definition types, no systems. |

## The four things worth knowing before reading the code

### The seed is the item, as far as its affixes are concerned

Two instances with the same definition and the same seed roll identically, in every process, for
ever. That is what lets the wire carry a definition id and a seed while a client's tooltip and a
realm's damage calculation agree — neither has to be sent a stat block, and neither can be lied to
about one.

Which means the roll must be a pure function of `(template, seed)` and nothing else: no clock, no
player, no ambient random. `TheSameSeedRollsTheSameAffixes` is the test.

⚠ **The pool's order is part of what a seed means.** A roll picks by weight from the item's affix
pool, so if that pool were in the order a designer happened to list it, tidying a YAML file would
re-roll every sword in the game. It is sorted by address instead, and
`TheAffixPoolIsSortedByAddressRatherThanByHowTheListWasWritten` is what keeps it that way.

⚠ **Adding an affix to a pool still re-rolls every item using it**, and there is no way around that —
the pool *is* part of the seed's meaning. It is a content decision with a visible consequence (a
player's sword changes), and the honest place to say so is here and in `ContentDiff`, not in a
migration that pretends otherwise.

### Rarity is a definition, and that is a deviation from the plan document

Doc 28's sketch authors `rarity: Legendary`. The two obvious readings are both wrong:

- a **closed C# enum** fixes every game to one ladder, which is exactly what a declinable library set
  is arranged to avoid;
- a **`GameplayTag`** is open, and orders *alphabetically* — so a bag sorted by rarity would put
  Common above Legendary for ever.

A rarity needs a number, and a number a designer sets is a definition. It still carries a tag, so
*every legendary drops a token* is a tag query like every other rule, and
`AnItemsRarityTagCountsAsOneOfItsTags` makes the tag reachable from the item.

⚠ **A slot is a tag and a rarity is an address, and the shapes differ on purpose.** A slot is asked
about hierarchically — *any one-handed weapon* — and never sorted. A rarity is sorted and never asked
about hierarchically. Each is the shape its questions want.

### One roll per affix, not per stat

*Of the Bear* grants health and armour. One roll drives both, so it rolls high on both or low on
both. Per-stat rolls would make one affix two independent gambles wearing one name, which is not what
a designer writing two ranges means — and `OneRollDrivesEveryStatOfAnAffix` pins it.

### Broken is zero durability; indestructible is zero *maximum* durability

Two fields one word apart, and reading them the wrong way round makes every stack of ore in the game
broken. `ItemStats.IsFunctional` is the one durability rule the engine ships — what breaking *costs*
is a game's, and a game that disagrees does not call it.

## What is owed

- **Sockets are declared and not filled.** `ItemDefinition.Sockets` is a count and
  `ItemTemplate.Sockets` reports it, but what is socketed is per-copy data of variable size, which a
  sixteen-byte instance deliberately cannot hold. It belongs in a side table kept by whatever owns
  the instance — which is the container, so it lands with `Vixen.Gameplay.Inventory`. The same
  applies to a transmog appearance override (G8) and a custom name.
- **Item *effects*.** Doc 28's example authors `effects: - !OnHit { … }`. An item granting an effect
  needs a trigger vocabulary — on hit, on equip, on use — and a trigger is a combat concept, so it
  lands with G2 rather than here.
- **A durability model.** Wear per hit, repair cost, and what a broken item does beyond granting
  nothing are all a game's rules; the engine ships the field and the one rule above.
