---
title: Dungeons, raids and lockouts
slug: gameplay/instances
kind: guide
area: Gameplay
summary: Difficulty tiers, encounters with gates and checkpoints, and lockouts whose resets are absolute boundaries rather than timers from whenever somebody entered.
api: [T:Vixen.Gameplay.Instances.LockoutScope, T:Vixen.Gameplay.Instances.LockoutReset, T:Vixen.Gameplay.Instances.EncounterStatus, T:Vixen.Gameplay.Instances.InstanceRefusal, T:Vixen.Gameplay.Instances.LockoutPolicyDefinition, T:Vixen.Gameplay.Instances.DifficultyDefinition, T:Vixen.Gameplay.Instances.EncounterDefinition, T:Vixen.Gameplay.Instances.InstanceDefinition, T:Vixen.Gameplay.Instances.LockoutPolicy, T:Vixen.Gameplay.Instances.Difficulty, T:Vixen.Gameplay.Instances.Instance, T:Vixen.Gameplay.Instances.Lockout, T:Vixen.Gameplay.Instances.ILockoutStore, T:Vixen.Gameplay.Instances.MemoryLockoutStore, T:Vixen.Gameplay.Instances.InstanceLibrary, T:Vixen.Gameplay.Instances.InstanceRun, T:Vixen.Gameplay.Instances.InstanceModule]
tags: [gameplay, instances, dungeons, raids, lockouts, mmo]
since: 0.1
status: preview
related: [gameplay/pvp, gameplay/requirements, gameplay/quests]
---

## What it is

An **`Instance`** is a map, its difficulties and the fights in it. An **`InstanceRun`** is one group's
attempt: which difficulty, who is in it, how far they got. A **`Lockout`** is what stops them doing it
again this week.

## What it is for

Doc 27's `Instance` shard kind with gameplay on top — dungeons, raids, and anything else a group is
given its own copy of.

## Using it

Compile an `InstanceLibrary`, call `InstanceRun.Enter`, then `Engage` and `Defeat` the fights.

⚠ **A reset is an absolute boundary.** "Weekly" is the same instant for everybody; a duration from
when somebody entered gives every player their own schedule and makes a raid night unplannable.

⚠ **The lockout is issued on the first defeat, not on entry**, once, and never extended. Everybody in
the party gets it.

⚠ **One locked-out member refuses the whole group**, because letting the rest in leaves somebody at
the door while their party clears without them.

⚠ **The difficulty is fixed for the life of the run.** A lockout is per `(instance, difficulty)`, so a
group that could switch halfway would have one lockout covering two.

⚠ **A wipe resets what was being fought and nothing else** — a dead boss stays dead. What it costs is
the attempt.

⚠ **A gate must fall before anything behind it may be engaged.**

## Examples

A dungeon on two difficulties:

```yaml
# Assets/Instances/crypt.vxdef
!InstanceDefinition
displayName: The Crypt
scene: maps/crypt
minimumPlayers: 2
maximumPlayers: 5
difficulties:
  - { id: normal, displayName: Normal }
  - id: heroic
    displayName: Heroic
    tag: Instance.Heroic
    healthScale: 2
    damageScale: 1.5
    lockout: { scope: Character, reset: Weekly }
    requires: [ { kind: HasTag, subject: Instance.Heroic } ]
encounters:
  - { id: gatekeeper, displayName: The Gatekeeper, isGate: true }
  - { id: lich, displayName: The Lich King }
```

Going in:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Instances;

static class Door {
    public static InstanceRefusal Open(
        Instance crypt,
        IReadOnlyList<PlayerId> party,
        ILockoutStore lockouts,
        double now,
        IRequirementContext attunement
    ) =>
        // One locked-out member refuses everybody, which is kinder than the alternative.
        InstanceRun.Enter(crypt, "heroic", party, lockouts, now, out _, attunement);
}
```

Fighting:

```csharp compile
using Vixen.Gameplay.Instances;

static class Boss {
    public static bool Kill(InstanceRun run, int encounter, ILockoutStore lockouts, double now) {
        if (run.Engage(encounter) != InstanceRefusal.None) {
            return false;
        }

        // The first defeat is what issues the lockout — walking in and leaving costs nothing.
        return run.Defeat(encounter, lockouts, now) == InstanceRefusal.None;
    }
}
```

## See also

- [PvP](gameplay/pvp) — G6's other half, and the same shape of match state.
- [Requirements](gameplay/requirements) — what gates a difficulty.
- [Quests](gameplay/quests) — what the run's kills advance.
