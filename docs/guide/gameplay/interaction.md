---
title: Interactables, gathering and crafting
slug: gameplay/interaction
kind: guide
area: Gameplay
summary: One channelled system for mining, looting, reading and pulling levers, with a claim that stops two players finishing the same rock — and recipes over it, whose stations are tag queries.
api: [T:Vixen.Gameplay.Interaction.InteractionInstancing, T:Vixen.Gameplay.Interaction.InterruptOn, T:Vixen.Gameplay.Interaction.InteractionRefusal, T:Vixen.Gameplay.Interaction.InteractableDefinition, T:Vixen.Gameplay.Interaction.Interactable, T:Vixen.Gameplay.Interaction.InteractionLibrary, T:Vixen.Gameplay.Interaction.Channel, T:Vixen.Gameplay.Interaction.InteractionResult, T:Vixen.Gameplay.Interaction.InteractionNode, T:Vixen.Gameplay.Interaction.InteractionModule, T:Vixen.Gameplay.Crafting.RecipeSource, T:Vixen.Gameplay.Crafting.CraftingRefusal, T:Vixen.Gameplay.Crafting.RecipeItemDefinition, T:Vixen.Gameplay.Crafting.RecipeDefinition, T:Vixen.Gameplay.Crafting.RecipeItem, T:Vixen.Gameplay.Crafting.Recipe, T:Vixen.Gameplay.Crafting.CraftingResult, T:Vixen.Gameplay.Crafting.CraftingLibrary, T:Vixen.Gameplay.Crafting.Crafter, T:Vixen.Gameplay.Crafting.CraftingModule]
tags: [gameplay, interaction, gathering, crafting, recipes, mmo]
since: 0.1
status: preview
related: [gameplay/loot, gameplay/requirements, gameplay/progression]
---

## What it is

An **`InteractableDefinition`** is a thing you use: a node, a chest, a door, a lever, a forge. An
**`InteractionNode`** is one of them in the world — how much is left of it and who is on it. A
**`Recipe`** is inputs, a station and outputs, and a **`Crafter`** is one character's knowledge of
them.

## What it is for

Doc 28 calls interaction *"the grinding loop"* and crafting *"recipes over that"*. Mining a node,
smelting at a forge, opening a chest, reading a book, flipping a lever and picking a herb are one
system with different definitions.

## Using it

Compile the two libraries, make an `InteractionNode` per thing in the world, and `Begin`, `Disturb`
and `Complete`. For crafting, `Learn`, `CanCraft` and `Craft`.

⚠ **A shared node is claimed for the duration of a channel** — without it two players both finish and
it yields twice.

⚠ **Interruption consumes nothing**, and **respawn counts from the completion that emptied it** rather
than from the last attempt.

⚠ **Per-player instancing puts the uses on the player**, which is the answer to node-stealing.

⚠ **A station is a tag query**, so an enchanted forge satisfies a recipe that asks for a forge.
`Vixen.Gameplay.Crafting` does not reference `Vixen.Gameplay.Interaction`: what it borrows is the
shape, not the code.

⚠ **Discovery is an exact match on the ingredients**, or throwing everything in the pot discovers
everything. ⚠ **Skill gain falls linearly to nothing across a band**, or the last recipe before a cliff
is the only one anybody makes.

⚠ **Neither library moves anything.** A node reports what it yields and a craft reports what to
consume; the caller's containers do the rest.

## Examples

A node and a recipe:

```yaml
# Assets/World/copper-vein.vxdef
!InteractableDefinition
displayName: Copper vein
tag: Interactable.Node.Ore
verb: Mine
channelSeconds: 3
uses: 2
respawnSeconds: 120
yields: loot/copper
requires: [ { kind: Value, subject: Profession.Mining, comparison: AtLeast, value: 25 } ]

# Assets/Recipes/copper-bar.vxdef
!RecipeDefinition
displayName: Copper bar
profession: Profession.Smithing
station: Interactable.Station.Forge
inputs:  [ { item: items/copper-ore, count: 2 } ]
outputs: [ { item: items/copper-bar } ]
skillRequired: 0
skillCap: 100
skillGain: 4
```

Mining it:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Interaction;

static class Mining {
    public static InteractionRefusal Swing(InteractionNode vein, PlayerId who, float now) =>
        // Claimed from here until it completes, is interrupted, or the claimant walks off.
        vein.Begin(who, now);

    public static bool Hit(InteractionNode vein) =>
        // The definition says whether being hit stops it; nothing is consumed either way.
        vein.Disturb(InterruptOn.Damage);

    public static DefId Finish(InteractionNode vein, PlayerId who, float now) =>
        vein.Complete(who, now, out var result) == InteractionRefusal.None ? result.Yields : DefId.None;
}
```

Crafting, and finding something by experiment:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Crafting;

static class Forge {
    public static CraftingResult? Smelt(
        Crafter smith,
        Recipe bar,
        GameplayTagSet station,
        IReadOnlyDictionary<uint, int> holdings,
        ulong attempt
    ) =>
        smith.Craft(bar, station, holdings, attempt, out var result) == CraftingRefusal.None ? result : null;

    public static Recipe? Experiment(Crafter smith, IReadOnlyList<RecipeItem> pot) =>
        // An exact match — a superset would discover everything at once.
        smith.TryDiscover(pot, out var found) ? found : null;
}
```

## See also

- [Loot](gameplay/loot) — what a node's `yields` address names.
- [Progression](gameplay/progression) — what answers a recipe's skill.
- [Requirements](gameplay/requirements) — what gates both.
