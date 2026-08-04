---
title: Abilities and damage
slug: gameplay/combat
kind: guide
area: Gameplay
summary: Abilities over the kernel's effects, and a damage pipeline of six named stages a game inserts rules into rather than replaces.
api: [T:Vixen.Gameplay.Combat.AbilityDefinition, T:Vixen.Gameplay.Combat.DamageDefinition, T:Vixen.Gameplay.Combat.AbilityCostDefinition, T:Vixen.Gameplay.Combat.AbilityTargeting, T:Vixen.Gameplay.Combat.AbilityTemplate, T:Vixen.Gameplay.Combat.AbilityCost, T:Vixen.Gameplay.Combat.AbilityLibrary, T:Vixen.Gameplay.Combat.AbilityCaster, T:Vixen.Gameplay.Combat.AbilityTarget, T:Vixen.Gameplay.Combat.AbilityEvent, T:Vixen.Gameplay.Combat.AbilityEventKind, T:Vixen.Gameplay.Combat.AbilityFailure, T:Vixen.Gameplay.Combat.DamageStage, T:Vixen.Gameplay.Combat.DamageEvent, T:Vixen.Gameplay.Combat.IDamageRule, T:Vixen.Gameplay.Combat.DamagePipeline, T:Vixen.Gameplay.Combat.CombatAttributes, T:Vixen.Gameplay.Combat.BaseDamageRule, T:Vixen.Gameplay.Combat.CriticalStrikeRule, T:Vixen.Gameplay.Combat.ResistanceRule, T:Vixen.Gameplay.Combat.ShieldAbsorbRule, T:Vixen.Gameplay.Combat.HealthRule, T:Vixen.Gameplay.Combat.ThreatRule, T:Vixen.Gameplay.Combat.CombatResolver, T:Vixen.Gameplay.Combat.AbilityHit, T:Vixen.Gameplay.Combat.ThreatTable, T:Vixen.Gameplay.Combat.ThreatEntry, T:Vixen.Gameplay.Combat.CombatModule]
tags: [gameplay, combat, abilities, damage, threat]
since: 0.1
status: preview
related: [gameplay/effects, gameplay/attributes, gameplay/requirements]
---

## What it is

An **ability** is a definition: a cast time or a channel, a cooldown and charges, resource costs,
requirements, a targeting mode, some damage and some effects. An **`AbilityCaster`** owns one thing's
timing — what is ready, what is being cast, what it costs. A **`DamagePipeline`** is six named stages
a hit passes through. A **`ThreatTable`** is who a creature is angry with.

```
Compute → Crit → Mitigate → Absorb → Apply → React
```

## What it is for

Everything that hits something. The pipeline is the part worth having: a game inserts a rule at a
named point instead of writing its own damage function, so it gets crits, resistance, shields,
healing, threat and death without reimplementing any of them — and can still change how any one of
them works.

Threat is here rather than in a game because doc 28 says so in as many words: *every game that adds
threat later adds it wrong.* The failure is always the same — threat becomes "whoever hit hardest",
and then a taunt has no meaning and a tank swap cannot be authored.

## Using it

Compile a catalog into an `AbilityLibrary`, give each combatant an `AbilityCaster`, tick it, and hand
completed abilities to a `CombatResolver` with the targets whatever owns the world resolved.

⚠ **This library never asks where anything is.** `AbilityTarget` carries a distance the caller
computed, and turning a cone into a list of victims is the caller's job. What it validates is the
*rule* — a targeted ability has a target, and the distance it was given is inside its range.

⚠ **Costs are paid on completion, and a channel pays per tick.** An interrupted cast therefore
refunds nothing, because it took nothing.

⚠ **A silence ends a cast already in flight.** Blocking only new casts lets a three-second cast finish
after its caster was silenced two seconds in.

⚠ **`CanBegin` is the same check `TryBegin` runs**, so a greyed-out button and a rejected request give
the same reason — and the reasons are ordered longest-lived first, so a silenced player is told about
the silence rather than about the global cooldown.

⚠ **Health is a base value, not a modifier.** Taking damage is not undone when the thing that caused
it expires.

## Examples

An ability:

```yaml
# Assets/Abilities/fireball.vxdef
!AbilityDefinition
displayName: Fireball
targeting: Target
range: 30
castTime: 2
cooldown: 6
costs:
  - { attribute: Mana, amount: 50 }
damage:
  school: Damage.Fire
  amount: 100
  scalesWith: Power
  coefficient: 1
appliesToTarget: [ effects/burning ]
tags: [ Ability.Cast.Fireball ]
```

Casting it, and resolving what it hit:

```csharp compile
using System.Collections.Generic;
using Vixen.Gameplay;
using Vixen.Gameplay.Combat;

static class Casting {
    public static void Step(
        AbilityCaster caster,
        CombatResolver resolver,
        IReadOnlyList<AbilityTarget> victims,
        float delta,
        ulong eventId
    ) {
        var events = new List<AbilityEvent>();

        caster.Tick(delta, events);

        foreach (var entry in events) {
            // A cast that completed, or a channel that came due, is what actually does something.
            if (entry.Kind is AbilityEventKind.Completed or AbilityEventKind.Ticked) {
                resolver.Resolve(caster.Abilities.Get(entry.Ability), caster.Subject, victims, eventId);
            }
        }
    }

    // The same answer the realm would give, for a button's tooltip.
    public static AbilityFailure Why(AbilityCaster caster, DefId ability, AbilityTarget at) =>
        caster.CanBegin(ability, at);
}
```

A game's own rule, at a named stage:

```csharp compile
using Vixen.Gameplay.Combat;

// "Damage taken while below a quarter health is halved." A rule at a named point, rather than a
// replacement pipeline with none of the tested edge cases in it.
public sealed class LastStandRule(CombatAttributes attributes) : IDamageRule {
    public DamageStage Stage => DamageStage.Mitigate;

    // After the shipped resistance rule, which is Order 0.
    public int Order => 10;

    public void Apply(ref DamageEvent hit) {
        if (hit.IsHealing) {
            return;
        }

        var health = hit.Target.Attributes.ValueOf(attributes.Health);
        var maximum = hit.Target.Attributes.ValueOf(attributes.MaximumHealth);

        if (maximum > 0f && health < maximum * 0.25f) {
            hit.Mitigated += hit.Amount * 0.5f;
            hit.Amount *= 0.5f;
        }
    }
}
```

Threat, and a taunt that works:

```csharp compile
using Vixen.Gameplay.Combat;

static class Encounter {
    public static ulong WhoIsTheBossHitting(ThreatTable table, in AbilityHit hit, ulong attacker) {
        table.Add(attacker, hit.Threat);

        return table.Target();
    }

    // Not a large threat number: the taunter is forced for the duration, so nothing out-damages it.
    public static void Taunt(ThreatTable table, ulong tank) => table.Taunt(tank, 3f);
}
```

## See also

- [Effects](gameplay/effects) — what an ability applies, and what silences it.
- [Attributes](gameplay/attributes) — where the numbers a hit reads come from.
- [Requirements](gameplay/requirements) — what an ability checks before it starts.
