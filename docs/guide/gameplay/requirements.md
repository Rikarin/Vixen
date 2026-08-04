---
title: Requirements
slug: gameplay/requirements
kind: guide
area: Gameplay
summary: "Can I do this" answered once, by code the client runs for the tooltip and the realm runs for the truth.
api: [T:Vixen.Gameplay.RequirementKind, T:Vixen.Gameplay.RequirementComparison, T:Vixen.Gameplay.RequirementDefinition, T:Vixen.Gameplay.Requirement, T:Vixen.Gameplay.IRequirementContext, T:Vixen.Gameplay.RequirementSet, T:Vixen.Gameplay.GameplaySubject]
tags: [gameplay, requirements, conditions, prediction]
since: 0.1
status: preview
related: [gameplay/tags, gameplay/attributes, gameplay/effects]
---

## What it is

A **requirement** is a tag query or a numeric predicate: *level at least 80*, *has
`Profession.Smithing`*, *does not have `State.InCombat`*, *at least 500 gold*. A `RequirementSet` is a
list of them, evaluated as a conjunction, against an `IRequirementContext` — usually a
`GameplaySubject`, which is one thing's stats, tags and running effects.

## What it is for

Making a greyed-out button and a rejected request agree. The same compiled requirement is evaluated
on the client for the UI and on the realm for the truth, out of one assembly. Two implementations of
"can I do this" is how a player learns to spam a button that says no.

Abilities, recipes, vendors, quests, instances, mounts, housing permissions and matchmaking
eligibility all ask the same question, so they all use this rather than each growing a predicate.

## Using it

Author `RequirementDefinition`s inside whatever definition needs them, compile the list once against
the tag table, and evaluate it.

⚠ **A value the context does not have counts as zero, not as a pass.** A requirement about a currency
an archetype does not track has to fail; the alternative is a vendor that sells to anyone whose
account is missing a column.

⚠ **A requirement about a tag the content does not have fails closed too** — `HasTag` fails,
`NotHasTag` passes.

`TryFindUnmet` is what a tooltip is written from. "Requires level 80" is a better answer than a grey
button, and because it is the same evaluation the realm runs, the two cannot say different things.

The list is always **and**. A disjunction is a [`GameplayTagQuery`](gameplay/tags)'s `any` list; a
list that could mean "or" would need a grouping syntax and an evaluation order nobody could see.

## Examples

The plan document's own example, compiled:

```csharp compile
using Vixen.Gameplay;

static class Smithing {
    // requires: [ Level >= 80, HasTag(Profession.Smithing), NotHasTag(State.InCombat) ]
    public static RequirementSet Compile(GameplayTagTable tags) =>
        RequirementSet.Compile(
            [
                new() {
                    Kind = RequirementKind.Value,
                    Subject = "Level",
                    Comparison = RequirementComparison.AtLeast,
                    Value = 80f
                },
                new() { Kind = RequirementKind.HasTag, Subject = "Profession.Smithing" },
                new() { Kind = RequirementKind.NotHasTag, Subject = "State.InCombat" }
            ],
            tags
        );
}
```

The client's half and the realm's half, which are the same call:

```csharp compile
using Vixen.Gameplay;

static class Tooltip {
    public static string Why(RequirementSet requirements, GameplaySubject player) =>
        requirements.TryFindUnmet(player, out var unmet)
            ? unmet.Kind switch {
                RequirementKind.Value => $"Requires {unmet.Subject} {unmet.Comparison} {unmet.Value}",
                RequirementKind.HasTag => "You are missing something.",
                _ => "Not while you are in that state."
            }
            : "Craft";
}
```

A subject a game supplies itself, when the numbers are not stats:

```csharp compile
using Vixen.Gameplay;

// A currency lives in a durable row rather than in an AttributeSet, which is why the context is an
// interface and not a dictionary the kernel keeps.
public sealed class Wallet(GameplayTagSet tags, float gold) : IRequirementContext {
    public GameplayTagSet? Tags => tags;

    public bool TryGetValue(AttributeId subject, out float value) {
        if (subject == AttributeId.From("Currency.Gold")) {
            value = gold;

            return true;
        }

        value = 0f;

        return false;
    }
}
```

## See also

- [Gameplay tags](gameplay/tags) — the tag half of a requirement.
- [Attributes](gameplay/attributes) — the numeric half.
- [Effects](gameplay/effects) — what a `GameplaySubject` ticks.
