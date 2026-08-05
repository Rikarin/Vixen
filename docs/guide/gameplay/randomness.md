---
title: Gameplay randomness
slug: gameplay/randomness
kind: guide
area: Gameplay
summary: A reproducible stream seeded per event, so "the log says you rolled a 3" is answerable a year later.
api: [T:Vixen.Gameplay.GameplayRandom]
tags: [gameplay, random, loot, determinism]
since: 0.1
status: preview
related: [gameplay/definitions, gameplay/effects]
---

## What it is

`GameplayRandom` is a PCG-XSH-RR generator over a 64-bit LCG: a struct with eight bytes of state, no
allocation, and a stream that is a pure function of its seed.

`GameplayRandom.For(eventId, salt)` is the shape nearly every caller wants — a stream for *this* drop,
*this* craft, *this* encounter, and the salt picks which roll within it.

## What it is for

Being able to recompute a roll. A support ticket about a drop, a report about a crit, a dispute about
crafting quality: all of them are answerable only if the roll can be reproduced from something that
was written down, and a drop event's id is written down.

It is not for anything a player must not be able to predict. A stream whose seed is a logged event id
is reproducible by definition, which is the point; anything security-relevant wants a cryptographic
source instead.

## Using it

Seed from the event, not from the clock. Salt per roll so that "which item" and "what quality" are
different streams rather than consecutive draws that a caller can accidentally reorder.

`State` is enough to resume a stream exactly, which is what a durable sequence — a pity counter's
stream, a persistent world event's — stores.

⚠ **Seeds are mixed, never combined with an operator.** `id ^ salt` and `id + salt` both have inputs
that cancel: `Vixen.Ai`'s `AgentRandom` shipped with an XOR that made every agent in the world draw
the same number, because the seed and the entity were the same hash. Everything here goes through
SplitMix64's finaliser, which has no such pair.

⚠ **`NextInt` is debiased by rejection, not by a modulo.** A plain `% bound` makes the low values
likelier by up to one part in `2³² / bound` — invisible on a coin flip and measurable on a loot table,
which is exactly where this gets used.

`NextFloat` is strictly below one, so `Chance(1f)` always happens and `Chance(0f)` never does, both
exactly.

## Examples

A drop, reproducible from its event id:

```csharp compile
using Vixen.Gameplay;

static class Loot {
    public static int Roll(ulong dropEventId, float[] weights) {
        // Salt 0 is which item; salt 1 would be its quality. Different streams, so adding a roll
        // later does not shift the ones already logged.
        var random = GameplayRandom.For(dropEventId, 0);

        return random.Pick(weights);
    }
}
```

Resuming a stream that outlives a process:

```csharp compile
using Vixen.Gameplay;

static class Pity {
    public static (bool Hit, ulong State) Draw(ulong stored, float chance) {
        var random = GameplayRandom.Resume(stored);
        var hit = random.Chance(chance);

        return (hit, random.State);
    }
}
```

## See also

- [Definitions](gameplay/definitions) — where a loot table's weights are authored.
- [Effects](gameplay/effects) — the other place a per-event roll shows up.
