---
title: Dynamic events and world bosses
slug: gameplay/dynamic-events
kind: guide
area: Gameplay
summary: A quest stage with the scope moved off one player and onto a realm — contribution tiers instead of tap-ownership, monotone scaling, and success and failure both leading somewhere.
api: [T:Vixen.Gameplay.Quests.DynamicEventDefinition, T:Vixen.Gameplay.Quests.EventScalingDefinition, T:Vixen.Gameplay.Quests.ContributionTierDefinition, T:Vixen.Gameplay.Quests.EventScheduleDefinition, T:Vixen.Gameplay.Quests.DynamicEventTemplate, T:Vixen.Gameplay.Quests.DynamicEventInstance, T:Vixen.Gameplay.Quests.DynamicEventStatus, T:Vixen.Gameplay.Quests.ContributionTier, T:Vixen.Gameplay.Quests.EventLink, T:Vixen.Gameplay.Quests.DynamicEventDirector, T:Vixen.Gameplay.Quests.EventChainStep]
tags: [gameplay, events, world-boss, scaling, contribution, mmo]
since: 0.1
status: preview
related: [gameplay/quests, gameplay/events, gameplay/quest-editor]
---

## What it is

A **dynamic event** is a quest stage owned by a realm rather than by a player: everybody's kills count
towards one number, everybody who did enough is paid, and **both** success and failure lead somewhere.
A **`DynamicEventDirector`** runs them and walks the chains. A **world boss** is one of these with a
schedule.

## What it is for

The Guild Wars 2 shape. A camp that falls, is retaken and falls again is a place things happen to
rather than a list of things to do — and the mechanism for that is one dictionary and one loop, since
the objectives are already the quest half's.

## Using it

Author the objectives exactly as a quest stage's. Add a duration, tiers and the two branches. `Begin`
one, `Tick` the director, and read `Stepped` to see what an ending started.

⚠ **Contribution tiers, not tap-ownership.** Deciding who "gets" an event by first hit, most damage or
killing blow is what makes a world boss a race and a passer-by an intruder. `Contribute` is also how a
game credits work no objective counts — healing, reviving, repairing a wall.

⚠ **Scaling is monotone by construction:** one clamped linear term, no table and no per-band override.
A shape a designer can bend is one that can bend downwards, and an event that got *easier* when a
tenth player arrived is a mechanic for griefing it.

⚠ **Rescaling raises and never lowers**, so a player logging out cannot complete an objective.

⚠ **The clock is checked after the objectives.** Finishing on the very tick the duration runs out
succeeded; the work was done, and the other order reads as the server cheating.

⚠ **A chain may cycle and nothing tries to prevent it.** What is bounded is one resolution:
`DynamicEventDirector.MaximumChainDepth` is a backstop against content whose events all end the
instant they begin, not a limit on how long a chain may be.

⚠ **An event already running is not started again.** Two failures both branching to "retake the camp"
is ordinary authoring; the other reading gives two instances with half the participants each.

⚠ **The schedule is authored here and enacted elsewhere.** Doc 28 puts a world boss's schedule in
`Live.Instances.Cluster` because it is fleet-wide: a schedule one realm decides is a boss every shard
sees differently. Nothing in this library looks at a clock.

## Examples

A camp that can fall:

```yaml
# Assets/Events/camp-defence.vxdef
!DynamicEventDefinition
displayName: Defend the camp
scene: maps/queensdale
duration: 60
objectives:
  - { type: Kill, count: 10, targetTags: [ Creature.Bandit ] }
scaling: { baseParticipants: 5, perParticipant: 0.2, maximum: 3 }
tiers:
  - { displayName: Gold, minimum: 50, rewards: { experience: 900 } }
  - { displayName: Silver, minimum: 20 }
onFailure: [ events/camp-retake ]   # and retake's onSuccess points back here
```

Running the chain:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Quests;

static class Camp {
    public static DynamicEventDirector Start(QuestLibrary library, GameplayEventBus bus) {
        var director = new DynamicEventDirector(library, bus);

        // Success and failure both lead somewhere, which is the whole point.
        director.Stepped += step => Announce(step);
        director.Begin(DefId.From("events/camp-defence"));

        return director;
    }

    static void Announce(EventChainStep step) { }
}
```

Paying everybody who did enough:

```csharp compile
using Vixen.Gameplay.Quests;

static class Payout {
    public static int Settle(DynamicEventInstance instance) {
        if (instance.Status != DynamicEventStatus.Succeeded) {
            return 0;
        }

        var paid = 0;

        // One player's tier is not another player's loss — everyone who reached one gets it.
        foreach (var (participant, contribution) in instance.Contributions) {
            if (instance.Template.TierFor(contribution) is { } tier) {
                Give(participant, tier.Reward);
                paid++;
            }
        }

        return paid;
    }

    static void Give(ulong participant, QuestReward reward) { }
}
```

## See also

- [Quests](gameplay/quests) — the same objectives, owned by one player.
- [The quest and chain editor](gameplay/quest-editor) — why the chain is drawn the way it is.
- [Gameplay events](gameplay/events) — what the objectives are waiting for.
