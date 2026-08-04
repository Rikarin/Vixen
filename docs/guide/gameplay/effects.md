---
title: Effects
slug: gameplay/effects
kind: guide
area: Gameplay
summary: Buff, debuff, damage-over-time, stun, aura, shield, stance and mount are one type with a stacking policy.
api: [T:Vixen.Gameplay.EffectStacking, T:Vixen.Gameplay.ModifierDefinition, T:Vixen.Gameplay.EffectDefinition, T:Vixen.Gameplay.EffectTemplate, T:Vixen.Gameplay.EffectHandle, T:Vixen.Gameplay.EffectEventKind, T:Vixen.Gameplay.EffectEvent, T:Vixen.Gameplay.ActiveEffect, T:Vixen.Gameplay.EffectSet]
tags: [gameplay, effects, buffs, stacking, duration]
since: 0.1
status: preview
related: [gameplay/attributes, gameplay/tags, gameplay/definitions, gameplay/randomness]
---

## What it is

An **effect** is anything with a duration. One definition type carries a duration, a tick period, a
stacking policy, a list of modifiers, tags it grants, tags it blocks, effects it makes the target
immune to, and events that end it. An `EffectSet` is every effect running on one thing, and it owns
that thing's tags and modifiers for as long as each effect lasts.

Doc 28 means this literally: a mount is an effect that grants `State.Mounted` and swaps a model; a
resurrection sickness, a crafting station's attunement, a PvP flag, a raid buff, a quest's timed
escort — all of them.

## What it is for

One replication path, one save path, one inspector, and one set of stacking bugs to fix once. Eight
systems that each grew their own timer is the arrangement this exists instead of.

The five stacking policies are the whole of the configuration:

| | |
|---|---|
| `None` | A second application is refused while the first runs. |
| `Refresh` | The duration goes back to full. One instance, one stack. |
| `Extend` | The full duration is added to what is left. |
| `StackTo` | One instance whose stack count rises to a maximum; modifiers scale with the count. |
| `Independent` | Every application is its own instance with its own clock. |

Stacking is per **(definition, instigator)**, so two casters buffing the same target get two
instances even under `Refresh`.

## Using it

Author an `EffectDefinition`, compile it once against the tag table with `EffectTemplate.Compile`,
and apply the template. Pass a list to collect `EffectEvent`s — the set reports and does not act.

⚠ **What a periodic tick *does* is not the kernel's.** Damage, healing and resource drain need a
damage pipeline, which is the combat library's; a tick here is an `EffectEventKind.Period` event in a
list the caller passed in.

⚠ **`BlockedTags` and `Immunities` are different questions.** Blocked tags stop the target *acting* —
a stun blocks `Ability.Cast`. Immunities stop something *being applied* to the target. Folding them
would make "silenced" and "immune to silence" one field.

"Until a condition" is `CancelOn`: a tag query over gameplay events, because that is what every such
condition actually is — until damaged, until they move, until they die.

## Examples

An effect, authored and compiled:

```csharp compile
using Vixen.Gameplay;

static class Burning {
    public static EffectDefinition Authored() => new() {
        DisplayName = "Burning",
        Duration = 6f,
        Period = 2f,
        Stacking = EffectStacking.StackTo,
        MaximumStacks = 3,
        Tags = ["Effect.Damage.Burning"],
        GrantedTags = ["State.Burning"],
        CancelOn = ["Event.Cleansed"],
        Modifiers = [new() { Attribute = "Power", Op = ModifierOp.AddPercent, Value = -0.1f }]
    };

    public static EffectTemplate Compile(GameplayTagTable tags) => EffectTemplate.Compile(Authored(), tags);
}
```

Applying it, and reading what happened:

```csharp compile
using System.Collections.Generic;
using Vixen.Gameplay;

static class Combat {
    public static int TicksThisFrame(GameplaySubject target, EffectTemplate burning, ulong caster, float delta) {
        var events = new List<EffectEvent>();

        target.Effects.Apply(burning, caster, events);
        target.Tick(delta, events);

        var ticks = 0;

        foreach (var entry in events) {
            // What a tick does is the caller's: the kernel has no damage pipeline and must not.
            if (entry.Kind == EffectEventKind.Period) {
                ticks++;
            }
        }

        return ticks;
    }
}
```

Immunity, blocking and cancellation, which are three different questions:

```csharp compile
using Vixen.Gameplay;

static class Control {
    public static bool CanCast(EffectSet effects, GameplayTag ability) => !effects.Blocks(ability);

    public static bool WouldLand(EffectSet effects, EffectTemplate stun) => !effects.IsImmuneTo(stun);

    public static int Interrupt(EffectSet effects, GameplayTag damaged) => effects.Notify(damaged);
}
```

## See also

- [Attributes](gameplay/attributes) — what an effect's modifiers act on.
- [Gameplay tags](gameplay/tags) — granted, blocked, immune and cancel-on are all tag queries.
- [Definitions](gameplay/definitions) — how an effect is authored and reaches a build.
- [Gameplay randomness](gameplay/randomness) — where a proc chance comes from.
