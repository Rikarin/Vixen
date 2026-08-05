---
title: Collections, achievements and transmog
slug: gameplay/collections
kind: guide
area: Gameplay
summary: Pets, mounts, appearances, titles and toys as one set of unlocked ids with a source — plus achievements, whose counted half rides the event bus and whose standing half is an ordinary requirement, and a wardrobe whose override falls back to the real item rather than to nothing.
api: [T:Vixen.Gameplay.Collections.CollectibleKind, T:Vixen.Gameplay.Collections.UnlockSource, T:Vixen.Gameplay.Collections.Unlock, T:Vixen.Gameplay.Collections.CollectibleDefinition, T:Vixen.Gameplay.Collections.Collectible, T:Vixen.Gameplay.Collections.AchievementCriterionDefinition, T:Vixen.Gameplay.Collections.AchievementDefinition, T:Vixen.Gameplay.Collections.AchievementCriterion, T:Vixen.Gameplay.Collections.Achievement, T:Vixen.Gameplay.Collections.CollectionLibrary, T:Vixen.Gameplay.Collections.CriterionProgress, T:Vixen.Gameplay.Collections.CollectionRecord, T:Vixen.Gameplay.Collections.Wardrobe, T:Vixen.Gameplay.Collections.CollectionsModule]
tags: [gameplay, collections, achievements, transmog, titles, mmo]
since: 0.1
status: preview
related: [gameplay/housing, gameplay/events, gameplay/items]
---

## What it is

A **`CollectibleDefinition`** is a pet, a mount, an appearance, a title, a toy or a cosmetic — all one
type, because all of them are the same thing: an id you either have or do not. A
**`CollectionRecord`** is one **account's** set of them, each with a recorded source. An
**`AchievementDefinition`** is the same thing with conditions on it. A **`Wardrobe`** is one
**character's** presentation over that account's collection.

## What it is for

Owning things, and the two questions that follow from it: what have I got, and what am I showing.

The account/character split is the one thing to keep straight. Unlocks are account-wide — a mount
earned on one character is owned by all of them — while transmog and titles are per character, which
is why they are two types and not one.

## Using it

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Collections;

static class Collecting {
    public static CollectionRecord Start(DefinitionCatalog catalog, GameplayEventBus bus) {
        var library = CollectionLibrary.Compile(catalog);
        var record = new CollectionRecord(library);

        // One subscription for every criterion in the build.
        record.Attach(bus);

        return record;
    }

    public static bool Drops(CollectionRecord record, Collectible mount, DefId boss) =>
        record.Unlock(mount, UnlockSource.Loot, boss);
}
```

Achievements have two halves, because one of them cannot count:

- **`Criteria`** — "kill thirty undead". These ride the kernel's [event bus](events.md): call
  `Attach` once and the record counts what it was asked about.
- **`Requires`** — "own fifty mounts", "be level twenty". Ordinary requirements, evaluated against the
  record's own tags composed with whatever context the caller supplies.

A count needs no special requirement kind: the record answers `Collection.Mount`,
`Collection.Total`, `Collection.Points` and `Collection.Earned` as requirement *values*, so "own fifty
mounts" is `Value Collection.Mount AtLeast 50`.

Combat posts `Event.Kill` with the victim's tags and nothing in it knows achievements exist.

⚠ **A criterion's tag query filters the *subject*, not the player.** "Kill thirty undead" is about the
victim; the player's own standing is `Requires`.

⚠ **An earned achievement never un-earns.** A refund, a sale or a patch that raises a threshold must
not take back something somebody already did.

⚠ **Nothing settles at construction.** `Refresh` is what asks, so a caller decides when notifications
fire rather than having them arrive during a load.

### The wardrobe

`wardrobe.Resolve(slot, worn)` is three rules, and the order matters:

1. A hidden slot shows nothing.
2. An override shows the appearance — **if it is still unlocked**.
3. Otherwise the worn item.

⚠ **An override to something no longer unlocked falls back to the real item, never to nothing.** An
appearance can be taken back, and the character wearing it must not turn invisible. `Resolve` checks
the unlock every single time rather than being told when one goes away, so there is no notification to
miss.

⚠ **Hiding and overriding are separate, and hiding wins.** "No helmet" and "a different helmet" are
different wishes; modelling the first as an override to nothing loses the chosen look the moment the
box is ticked.

## Examples

Earning cascades and stops, and a save is not a replay:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Collections;

static class Earning {
    public static int Slay(CollectionRecord record, GameplayTag kill, GameplayTagSet victim) {
        // One kill can finish an achievement, whose unlocked title finishes another, which unlocks a
        // toy. Resolved by a work queue, terminating because nothing is ever earned twice.
        record.Observe(new(kill, Amount: 30, Tags: victim));

        return record.Points;
    }

    public static CollectionRecord Load(CollectionLibrary library, CollectionRecord saved) {
        var loaded = new CollectionRecord(library);

        loaded.Restore(
            saved.Unlocks,
            saved.Achievements().Select(achievement => achievement.Id),
            saved.Counters()
        );

        return loaded;
    }
}
```

Re-running `Unlock` would re-derive achievements against today's content — so a patch that raised a
threshold would take back what somebody earned, and one that lowered it would hand out an achievement
with no notification anybody saw.

## See also

- [Player housing](housing.md) — the other half of G8, with the same no-clock rule.
- [Gameplay events](events.md) — where a criterion's counting comes from.
- [Items](items.md) — why the transmog override is a side table rather than a field on the instance.
