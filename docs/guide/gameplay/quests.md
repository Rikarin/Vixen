---
title: Quests, stages and objectives
slug: gameplay/quests
kind: guide
area: Gameplay
summary: A quest is stages, a stage is objectives, and an objective is a subscription with a tag query — so it costs nothing when nothing dies.
api: [T:Vixen.Gameplay.Quests.QuestDefinition, T:Vixen.Gameplay.Quests.QuestStageDefinition, T:Vixen.Gameplay.Quests.QuestObjectiveDefinition, T:Vixen.Gameplay.Quests.QuestRewardDefinition, T:Vixen.Gameplay.Quests.QuestGrantDefinition, T:Vixen.Gameplay.Quests.QuestRepeat, T:Vixen.Gameplay.Quests.IQuestObjective, T:Vixen.Gameplay.Quests.QuestObjectives, T:Vixen.Gameplay.Quests.QuestObjectiveRegistry, T:Vixen.Gameplay.Quests.QuestVerbs, T:Vixen.Gameplay.Quests.ObjectiveTemplate, T:Vixen.Gameplay.Quests.StageTemplate, T:Vixen.Gameplay.Quests.QuestTemplate, T:Vixen.Gameplay.Quests.QuestReward, T:Vixen.Gameplay.Quests.QuestGrant, T:Vixen.Gameplay.Quests.QuestLibrary, T:Vixen.Gameplay.Quests.ObjectiveTracker, T:Vixen.Gameplay.Quests.ObjectiveAdvance, T:Vixen.Gameplay.Quests.QuestJournal, T:Vixen.Gameplay.Quests.ActiveQuest, T:Vixen.Gameplay.Quests.QuestStatus, T:Vixen.Gameplay.Quests.QuestRefusal, T:Vixen.Gameplay.Quests.QuestChange, T:Vixen.Gameplay.Quests.QuestModule]
tags: [gameplay, quests, objectives, stages, rewards, mmo]
since: 0.1
status: preview
related: [gameplay/events, gameplay/dynamic-events, gameplay/requirements, gameplay/progression]
---

## What it is

A **quest** is stages; a **stage** is objectives; an **objective** is a *type* plus parameters. The
engine ships ten types — `Kill`, `Collect`, `Reach`, `Interact`, `Escort`, `Survive`, `Deliver`,
`Discover`, `Craft`, `Spend` — and a game adds an eleventh by implementing **`IQuestObjective`**.

A **`QuestJournal`** is one character's quests: what is taken, how far along, and what has finished.

## What it is for

Everything a player is told to go and do, without any of it polling. An objective subscribes to a
[gameplay event](gameplay/events) with a tag query and a scene filter, so "kill ten undead in
Queensdale" costs nothing at all until something dies.

## Using it

Compile a catalog into a `QuestLibrary`, read its `Problems`, and give each character a journal over
the shared bus. `Accept`, `Abandon` and `TurnIn` are the whole surface; progress happens by itself.

⚠ **The ten shipped types are nine verbs, one clock and one level.** The variety is in the *filter*,
which a designer writes — "kill ten undead" and "kill the Shatterer" are both `Kill`. Which is why a
game's own type is five lines.

⚠ **`Collect` is a level and the rest are tallies.** Having ten ore stops being true when nine are
sold; ten undead stay killed.

⚠ **Completion is latched.** Progress moves both ways; *finished* is a one-way door. Otherwise selling
an ore un-finishes a stage that has already advanced.

⚠ **A quest chain is a requirement and not a second mechanism.** Turning a quest in grants its `tag:`,
and the next quest asks `HasTag(Quest.Completed.Prologue)` — the same algebra a vendor uses, so the
greyed-out giver and the refused accept cannot disagree.

⚠ **`TurnIn` reports the reward and does not pay it.** Doc 28's authority table puts quest rewards in
the grain; a journal that handed out an item would be a realm deciding a durable question.

⚠ **The journal takes a requirement context and a tag set separately.** In a real game they are
different objects — the level is a progression state's, the tags are the character's — and
[`CompositeRequirementContext`](gameplay/events) is how a caller puts them together.

## Examples

A quest:

```yaml
# Assets/Quests/prologue.vxquest
!QuestDefinition
displayName: A Prologue
tag: Quest.Completed.Prologue      # what having finished it is
grantsTags: [ Quest.Active.Prologue ]
stages:
  - id: hunt
    displayName: Cull the skeletons
    objectives:
      - type: Kill
        displayName: Skeletons slain
        count: 3
        targetTags: [ Creature.Undead ]
        scene: maps/queensdale
  - id: gather
    objectives:
      - { type: Collect, count: 2, target: items/ore }
      - { type: Discover, optional: true }
rewards:
  experience: 500
  items: [ { def: items/sword } ]
  choices: [ { def: items/shield }, { def: items/wand } ]
```

Running one:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Quests;

static class Giver {
    public static QuestRefusal Accept(QuestJournal journal, DefId quest) {
        // The same call the client makes to grey the giver out, out of one assembly.
        var refusal = journal.CanAccept(quest);

        return refusal != QuestRefusal.None ? refusal : journal.Accept(quest);
    }

    public static void Complete(QuestJournal journal, DefId quest, int choice) {
        if (journal.TurnIn(quest, choice, out var reward) != QuestRefusal.None || reward is null) {
            return;
        }

        // What is owed, not what was paid: the caller's ledger transaction does that.
        foreach (var grant in reward.Items) {
            Pay(grant);
        }
    }

    static void Pay(QuestGrant grant) { }
}
```

A game's eleventh objective type:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Quests;

// Five lines, because an objective is a subscription and a counter and the seam says so.
public sealed class YodelObjective : IQuestObjective {
    public string Type => "Yodel";

    public string Verb => "Event.Yodel";
}

static class Registry {
    public static QuestObjectiveRegistry WithYodelling() =>
        new QuestObjectiveRegistry().AddShipped().Add(new YodelObjective());
}
```

## See also

- [Gameplay events](gameplay/events) — what an objective is waiting for.
- [Dynamic events](gameplay/dynamic-events) — the same machine with the scope moved to a realm.
- [Requirements](gameplay/requirements) — what gates an accept.
