---
title: Gameplay events
slug: gameplay/events
kind: guide
area: Gameplay
summary: A verb, a subject and a place, posted by whoever it happened to and filtered by whoever cares — the half of "features meet through tags and events" that tags alone could not carry.
api: [T:Vixen.Gameplay.GameplayEvent, T:Vixen.Gameplay.GameplayEventFilter, T:Vixen.Gameplay.GameplayEventBus, T:Vixen.Gameplay.GameplayEventCallback, T:Vixen.Gameplay.GameplayEventSubscription, T:Vixen.Gameplay.CompositeRequirementContext]
tags: [gameplay, events, bus, tags, requirements]
since: 0.1
status: preview
related: [gameplay/tags, gameplay/quests, gameplay/requirements]
---

## What it is

A **`GameplayEvent`** is a verb, a subject, a place, an amount, who caused it, and the subject's tags.
A **`GameplayEventFilter`** says which of them somebody wants. A **`GameplayEventBus`** is where they
are posted and where the filters wait.

**`CompositeRequirementContext`** is here too, because it was built for the same reason: a character's
state is several objects and a rule has to be able to ask all of them.

## What it is for

Letting two libraries meet without referencing each other. Doc 28's dependency spine says that where
loot has to drop from a raid encounter, or a quest has to count a kill, they meet *through tags and
events*. Combat posts `Event.Kill` with the victim's tags and never learns who was listening; a quest
objective is a subscription with a tag query and a scene filter, and it costs nothing when nothing
dies.

## Using it

Post from whatever the thing happened to. Subscribe with a filter built from a tag table. Cancel the
subscription when whatever wanted it is over.

⚠ **An empty verb range matches nothing, never everything.** A filter is built from a name a designer
wrote, and an unknown name resolves to an empty range. The other reading turns one typo into a rule
that fires on everything. Wanting every verb is `GameplayEventFilter.EveryVerb`, which has to be
written down.

⚠ **A subscription made during a dispatch does not see the event being dispatched.** This is the main
path, not an edge case: the last kill of a quest stage completes it, cancels its subscriptions and
takes out the next stage's — inside the handler the bus is calling. Letting the new subscription see
the current event counts one kill twice.

⚠ **`Tags` on an event is borrowed and must not be kept.** It is the subject's own live set, passed by
reference so that filtering a thousand kills allocates nothing.

## Examples

Posting a kill:

```csharp compile
using Vixen.Gameplay;

static class Deaths {
    public static void Report(GameplayEventBus bus, GameplayTagTable tags, GameplaySubject victim, ulong killer) {
        bus.Post(
            new GameplayEvent(tags.Resolve("Event.Kill"), Instigator: killer, Tags: victim.Tags)
        );
    }
}
```

Waiting for one, in a scene, with a tag query:

```csharp compile
using Vixen.Gameplay;

static class Watch {
    public static GameplayEventSubscription Undead(GameplayEventBus bus, GameplayTagTable tags, DefId scene) {
        var filter = new GameplayEventFilter(
            tags.RangeOf("Event.Kill"),
            Scene: scene,
            Tags: GameplayTagQuery.Resolve(tags, any: ["Creature.Undead"])
        );

        // A filter that cannot match is accepted rather than refused — reporting a verb the build does
        // not have belongs to whatever compiled it, which can name the definition.
        return bus.Subscribe(filter, (in GameplayEvent killed) => Counted(killed.Instigator));
    }

    static void Counted(ulong who) { }
}
```

Asking four questions of four owners:

```csharp compile
using Vixen.Gameplay;

static class Gate {
    public static bool CanBuy(RequirementSet requirements, GameplaySubject character, IRequirementContext progression) {
        // First answer wins for a value; any context suffices for a tag.
        var context = new CompositeRequirementContext(progression, character);

        return requirements.IsMetBy(context);
    }
}
```

## See also

- [Quests](gameplay/quests) — the first subscriber, and why the bus is in the kernel.
- [Gameplay tags](gameplay/tags) — what a verb and a filter are made of.
- [Requirements](gameplay/requirements) — what the composite context is for.
