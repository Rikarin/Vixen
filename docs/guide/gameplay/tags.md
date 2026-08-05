---
title: Gameplay tags
slug: gameplay/tags
kind: guide
area: Gameplay
summary: One hierarchical, interned tag type, numbered so that "is this under Damage.Fire" is two integer comparisons.
api: [T:Vixen.Gameplay.GameplayTag, T:Vixen.Gameplay.GameplayTagRange, T:Vixen.Gameplay.GameplayTagTable, T:Vixen.Gameplay.GameplayTagTableBuilder, T:Vixen.Gameplay.GameplayTagSet, T:Vixen.Gameplay.GameplayTagSet.Enumerator, T:Vixen.Gameplay.GameplayTagQuery]
tags: [gameplay, tags, content, rules]
since: 0.1
status: preview
related: [gameplay/definitions, gameplay/requirements, gameplay/effects]
---

## What it is

A **gameplay tag** is a dotted name — `Damage.Fire.Burn`, `Creature.Undead.Skeleton`,
`State.InCombat` — held as four bytes. A `GameplayTagTable` is every tag a build knows, baked into a
tree and numbered by a pre-order walk, so a tag's descendants occupy a contiguous range of numbers.

That numbering is the whole design. A `GameplayTagRange` is one tag and everything beneath it, and
asking whether a tag falls under a prefix is `index >= start && index < end`.

## What it is for

Rules a designer writes at the altitude they mean them. *Fire resistance reduces `Damage.Fire.*`.*
*Immune to control blocks `Effect.Control.*`.* *This quest counts `Creature.Undead.*`.*

Requirements, immunities, loot conditions, quest objectives, effect stacking, chat gating,
matchmaking eligibility, achievement criteria and interaction filters are all tag queries — which is
how the gameplay libraries stay opinionated without being closed. A game adds a rule by writing a tag
in a `.vxdef`, not by writing a class.

You do not use a tag where an identity is wanted. *Which item is this* is a
[`DefId`](gameplay/definitions); a tag says what kind of thing something is, and several things
share one.

## Using it

The table is baked by the content build out of every tag every definition mentions, plus whatever a
module declared for its own code. A game gets it from
[`DefinitionCatalog.Tags`](gameplay/definitions); building one by hand is what a test does.

Resolve authored prefixes **once**, at load, and keep the `GameplayTagRange`. That is what keeps the
frame path free of both the table and the string.

⚠ **A prefix the content does not have resolves to an empty range, which matches nothing.** The other
reading — an unknown prefix matching everything — is how a misspelling makes a boss immune to all
damage, and it reads correctly in review.

⚠ **Never persist a tag's index.** Adding a tag renumbers the ones after it, so a saved index means
something else after the next content build. Persist `GameplayTagTable.SymbolOf`, which is a hash of
the name.

## Examples

Baking a table and resolving a rule against it:

```csharp compile
using Vixen.Gameplay;

static class Vocabulary {
    public static GameplayTagTable Build() =>
        new GameplayTagTableBuilder()
            .Add("Damage.Fire.Burn")
            .Add("Damage.Frost")
            .Add("Creature.Undead.Skeleton")
            .Build();

    // Resolved once. Everything above Damage.Fire.Burn — Damage.Fire and Damage — is implied by
    // mentioning the leaf, so nothing has to declare an ancestor.
    public static GameplayTagRange FireDamage(GameplayTagTable tags) => tags.RangeOf("Damage.Fire");

    public static bool IsFire(GameplayTagRange fire, GameplayTag damage) => fire.Contains(damage);
}
```

A counted tag set, which is what an entity holds:

```csharp compile
using Vixen.Gameplay;

static class Stunning {
    // Two effects grant State.Stunned; one expires; the target is still stunned. A plain set loses
    // that, and the bug it produces reproduces once a week in a raid and never on a desk.
    public static bool StillStunned(GameplayTagTable tags) {
        var stunned = tags.Require("State.Stunned");
        var set = new GameplayTagSet();

        set.Add(stunned);
        set.Add(stunned);
        set.Remove(stunned);

        return set.Contains(stunned);
    }
}
```

A query, which is what a definition authors:

```csharp compile
using Vixen.Gameplay;

static class Eligibility {
    public static GameplayTagQuery UndeadInCombat(GameplayTagTable tags) =>
        GameplayTagQuery.Resolve(
            tags,
            all: ["Creature.Undead"],
            any: ["State.InCombat", "State.Mounted"],
            none: ["State.Invulnerable"]
        );
}
```

## See also

- [Definitions and the content walk](gameplay/definitions) — where the table comes from.
- [Requirements](gameplay/requirements) — a tag query plus a numeric predicate.
- [Effects](gameplay/effects) — granted tags, blocked tags and immunities, all tag queries.
