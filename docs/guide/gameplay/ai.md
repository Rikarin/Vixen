---
title: Leashing and spawn tables
slug: gameplay/ai
kind: guide
area: Gameplay
summary: How far a mob may be pulled before it goes home, and what lives in a camp — the two pieces of doc 28's AI section that did not already have a better home.
api: [T:Vixen.Gameplay.Ai.LeashDefinition, T:Vixen.Gameplay.Ai.Leash, T:Vixen.Gameplay.Ai.LeashState, T:Vixen.Gameplay.Ai.LeashBehaviour, T:Vixen.Gameplay.Ai.LeashVerdict, T:Vixen.Gameplay.Ai.SpawnTableDefinition, T:Vixen.Gameplay.Ai.SpawnEntryDefinition, T:Vixen.Gameplay.Ai.SpawnTable, T:Vixen.Gameplay.Ai.SpawnLibrary, T:Vixen.Gameplay.Ai.Spawner, T:Vixen.Gameplay.Ai.SpawnOrder, T:Vixen.Gameplay.Ai.GameplayAiModule]
tags: [gameplay, ai, leash, spawn, mmo]
since: 0.1
status: preview
related: [gameplay/combat, gameplay/instances, gameplay/randomness, gameplay/definitions]
---

## What it is

`Vixen.Gameplay.Ai` is two mechanisms: a **leash**, which decides how far a mob may be pulled from
where it belongs, and a **spawn table**, which decides what lives in a place and when it comes back.

It is much smaller than the name suggests, and the reason is worth knowing before you go looking for
something that is not here. Everything else doc 28 filed under AI already had a better home by the
time this library was written:

| What you might expect here | Where it actually is |
|---|---|
| Threat, aggro, taunt | `ThreatTable`, in [combat](combat.md) |
| Planners, blackboards, perception, action surfaces | `Core/Vixen.Ai` |
| Encounter scripting | An address on `EncounterDefinition`, in [instances](instances.md), pointing at a behaviour tree |
| **Leashing and spawn tables** | **Here** |

So this library references neither `Core/Vixen.Ai` nor `Vixen.Gameplay.Combat`. A leash is a distance
and a clock; a spawn table is a weighted pick.

## What it is for

### A leash has two radii, and that is the reason it is a type rather than a comparison

One radius makes a mob standing on the boundary flicker between chasing and resetting once per frame,
because the player is moving and the comparison keeps changing sides. The **tether** is where it
starts worrying; the **break** is where it gives up. Nothing happens in between.

⚠ **Coming back inside the *tether* clears it, not merely coming back inside the break.** Clearing on
the break is the same flicker with extra steps.

⚠ **Patience is the third case, and without it there is an exploit.** A mob held at exactly
tether-plus-one can be kited around a pillar for ever. The clock is what ends that.

⚠ **`HealsOnReset` is true by default**, and the default is the interesting part. A mob that keeps its
damage across a reset gets whittled down over a dozen pulls by one player who never has to win a
fight — the oldest exploit in the genre.

### A respawn timer starts at the death, not at the tick that noticed it

A server that fell behind would otherwise repopulate faster than one that did not, which is a
difference players feel and nobody can explain.

⚠ **The cap counts what is alive, not what has been spawned.** Counting spawns makes a camp that has
been cleared twice permanently empty.

⚠ **Jitter is not decoration.** A camp wiped in one pull comes back as one wave on a fixed timer for
ever, and every pull after the first is the same pull. It is deterministic per spawner, so a replay
still matches — see [randomness](randomness.md).

### It says what to spawn and never where

Placing something needs the scene and a navigation mesh. That is the boundary every library in this
framework sits on, and the same one `PvpMatch.Occupy` and `InteractionNode` sit on.

## Using it

A leash is authored as content, compiled into a `Leash`, and asked one question per tick: *how far
away is the target, and what time is it?*

```csharp compile
using Vixen.Gameplay.Ai;

static class Leashing {
    public static bool ShouldGoHome(Leash leash, float distanceFromHome, float now) {
        // Two radii and a clock, in one call. The verdict carries the state, whether it just changed,
        // and the one answer a caller usually wants.
        var verdict = leash.Check(distanceFromHome, now);

        return verdict.ShouldReset;
    }
}
```

⚠ **`LeashVerdict.Changed` is what an announcement hangs on**, not `State`. A mob that has been
`Stretched` for four seconds reports `Stretched` on every one of those ticks, and a caller that
reacted to the state rather than the transition would play the evade animation two hundred times.

`Leash.Release()` is what a kill or a reset calls: it puts the state back to `Held` and stops the
patience clock. The clock itself is readable as `StretchedFor(now)`, so a UI can show it.

A spawner is a camp — a table and a seed — and it fills a collection rather than allocating one:

```csharp compile
using System.Collections.Generic;
using Vixen.Gameplay.Ai;

static class Camps {
    public static int Repopulate(Spawner spawner, List<SpawnOrder> orders, float now) {
        orders.Clear();

        // The order names a creature, a count and a slot. Where it goes is the scene's answer.
        return spawner.Tick(now, orders);
    }
}
```

⚠ **Tell the spawner when something died, with the time it died.** `Died(slot, now)` is what starts
the respawn clock, and passing the tick that *noticed* rather than the moment it happened makes a
server that fell behind repopulate faster than one that did not.

## Examples

The three leash states, in the order a pull walks through them:

```csharp compile
using Vixen.Gameplay.Ai;

static class LeashCases {
    public static LeashState Walk(Leash leash) {
        // Inside the tether: held, and nothing to say.
        leash.Check(leash.Tether - 1f, now: 0f);

        // Past the tether and inside the break: stretched, and the patience clock is running.
        leash.Check(leash.Tether + 1f, now: 1f);

        // Past the break: broken, whatever the clock says.
        return leash.Check(leash.Break + 1f, now: 2f).State;
    }
}
```

⚠ **Coming back inside the *tether* is what clears it** — not coming back inside the break. Clearing
on the break puts the mob on a boundary where one step re-triggers it, which is the flicker the two
radii exist to prevent.

A camp that keeps its cap without counting spawns:

```csharp compile
using System.Collections.Generic;
using Vixen.Gameplay.Ai;

static class Barrow {
    public static int Run(Spawner spawner, List<SpawnOrder> orders, float now) {
        // The cap is on what is alive. Counting what has been *spawned* makes a camp that has been
        // cleared twice permanently empty.
        while (!spawner.IsFull && spawner.Tick(now, orders) > 0) {
            foreach (var order in orders) {
                _ = order.Creature;
            }

            orders.Clear();
        }

        return spawner.Alive;
    }
}
```

## See also

- [Combat](combat.md) — `ThreatTable`, which is where aggro actually lives.
- [Instances](instances.md) — `EncounterDefinition`, and where encounter scripting hangs.
- [Randomness](randomness.md) — why the spawn jitter is deterministic.
- [Definitions](definitions.md) — how a spawn table is authored.
