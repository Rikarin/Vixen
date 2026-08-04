---
title: Levels, talents and reputation
slug: gameplay/progression
kind: guide
area: Gameplay
summary: Definitions plus one durable record, with a talent allocation validated whole because a client-built talent tree is a client-chosen power level.
api: [T:Vixen.Gameplay.Progression.ExperienceCurveDefinition, T:Vixen.Gameplay.Progression.ExperienceCurve, T:Vixen.Gameplay.Progression.ExperienceGain, T:Vixen.Gameplay.Progression.TalentTreeDefinition, T:Vixen.Gameplay.Progression.TalentNodeDefinition, T:Vixen.Gameplay.Progression.TalentPrerequisiteDefinition, T:Vixen.Gameplay.Progression.TalentTree, T:Vixen.Gameplay.Progression.TalentNode, T:Vixen.Gameplay.Progression.TalentAllocation, T:Vixen.Gameplay.Progression.TalentVerdict, T:Vixen.Gameplay.Progression.TalentRejection, T:Vixen.Gameplay.Progression.SpecialisationDefinition, T:Vixen.Gameplay.Progression.Specialisation, T:Vixen.Gameplay.Progression.ProfessionDefinition, T:Vixen.Gameplay.Progression.ProfessionTierDefinition, T:Vixen.Gameplay.Progression.ReputationDefinition, T:Vixen.Gameplay.Progression.ReputationRankDefinition, T:Vixen.Gameplay.Progression.RankedTrack`1, T:Vixen.Gameplay.Progression.ProgressionLibrary, T:Vixen.Gameplay.Progression.ProgressionState, T:Vixen.Gameplay.Progression.ProgressionModule]
tags: [gameplay, progression, levels, talents, reputation, rpg]
since: 0.1
status: preview
related: [gameplay/requirements, gameplay/attributes, gameplay/tags]
---

## What it is

A **curve** says what each level costs. A **talent tree** is a DAG of nodes with ranks, costs, row
gates and prerequisites. A **specialisation** is one of a set. A **profession** and a **faction** are
each one number and the ranks it passes through. A **`ProgressionState`** is all of that for one
character — and it is an `IRequirementContext`, which is what makes doc 28's own example resolve:

```
requires: [ Level >= 80, HasTag(Profession.Smithing), NotHasTag(State.InCombat) ]
```

## What it is for

Everything a character accumulates that is not an item. Levels and XP, the points they spend, the
skills they grind and the factions they please — and, because the state answers requirement queries,
every gate in the game asks about them with the same algebra a vendor and an ability use.

## Using it

Compile a catalog into a `ProgressionLibrary`, give each character a `ProgressionState`, and award
into it. Read `Modifiers` to get everything the progression grants as one modifier source, so
respeccing removes it exactly.

⚠ **A talent allocation is validated whole.** The client sends what it thinks it has taken and the
server checks it from scratch against the points the character actually has. Doc 28: *a client-built
talent tree is a client-chosen power level.*

⚠ **That forces every rule to be a property of the allocation.** A row gate is a total and checks
fine; "A before B" is a property of a sequence and is not expressible — nor missed, since it is
unverifiable after a respec.

⚠ **A row gate counts the rows *above* it.** Counting the whole tree lets the point being spent on a
row be the point that opens it, so a three-point gate becomes a two-point one.

⚠ **A rank multiplies the value rather than repeating the modifier** — five +2 % modifiers from one
source cannot be told apart on removal and compose wrongly in the multiplicative bucket.

⚠ **The same string names a track's tag and its number.** `Profession.Smithing` is both.

⚠ **Gear score is a number a game sets.** Averaging an item level needs the inventory, and this
library takes no dependency on it.

## Examples

A talent tree:

```yaml
# Assets/Talents/fire.vxdef
!TalentTreeDefinition
displayName: Fire
nodes:
  - id: kindle
    displayName: Kindle
    maximumRanks: 3
    modifiers: [ { attribute: Power, op: AddPercent, value: 0.02 } ]
  - id: blaze
    displayName: Blaze
    requiredPoints: 3          # three points in rows above this one
    requires: [ { node: kindle, ranks: 2 } ]
    modifiers: [ { attribute: CritChance, op: Add, value: 0.05 } ]
```

Accepting a client's allocation:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Progression;

static class Respec {
    public static string Apply(ProgressionState character, DefId tree, TalentAllocation claimed) {
        // Checked from scratch against the points they actually have, and rejected as a unit.
        var verdict = character.Allocate(tree, claimed);

        return verdict.IsLegal ? "applied" : verdict.Message;
    }
}
```

Everything a progression grants, as one removable source:

```csharp compile
using System.Collections.Generic;
using Vixen.Gameplay;
using Vixen.Gameplay.Progression;

static class Sheet {
    public static void Apply(ProgressionState character, AttributeSet stats) {
        var source = ModifierSource.From(DefId.From("progression"), 1);
        var modifiers = new List<Modifier>();

        // Off first, so recomputing after a respec cannot double anything.
        stats.RemoveBySource(source);
        character.Modifiers(source, modifiers);

        foreach (var modifier in modifiers) {
            stats.Add(modifier);
        }
    }
}
```

Awarding experience and reputation:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Progression;

static class Rewards {
    public static int ForAQuest(ProgressionState character, DefId faction) {
        var gain = character.Award(650);

        character.Earn(faction, 250);
        character.Train(DefId.From("professions/smithing"), 1);

        // Levels gained is what a caller applies level-up effects for; wasted experience is what a
        // caller shows as "you are at the cap".
        return gain.Levels;
    }
}
```

## See also

- [Requirements](gameplay/requirements) — what a `ProgressionState` answers.
- [Attributes](gameplay/attributes) — where its modifiers land.
- [Gameplay tags](gameplay/tags) — the rank and tier tags it grants.
