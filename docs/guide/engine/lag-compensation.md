---
title: Lag compensation
slug: engine/lag-compensation
kind: guide
area: Networking
summary: Judging a shot against the world the shooter actually saw, and getting the ring filled without anybody remembering to fill it.
api: [T:Vixen.Net.Physics.LagCompensationSystem, T:Vixen.Net.Physics.LagCompensated]
tags: [networking, physics, lag-compensation, shooting]
since: 0.1
status: preview
related: [engine/networked-players, engine/smoothing-received-motion, engine/round-trip-and-jitter]
---

## What it is

A **`LagCompensationSystem`** records where every compensated body was, once per tick, into a
`LagCompensator`'s ring. A **`LagCompensated`** tag on an entity is what puts its body in that ring.

Together they are the half of lag compensation that is not a decision. The other half — rewinding to
the tick a shooter claimed and asking physics a question there — stays with the game, because only
the game knows what a shot is.

## What it is for

A player with an 80 ms round trip aims at a running target, fires, and the packet reaches the server
40 ms later. By then the target has moved a third of a metre and the shot misses something the
shooter watched themselves hit. Snapshot interpolation makes it worse rather than better: the client
was not rendering the newest state it held, it was rendering behind it.

Tag what gets shot at — players, vehicles, anything moving fast enough that a client's view of it is
meaningfully behind the server's. Do not tag the level. A wall's history is thirty-two copies of one
pose, and rewinding it costs the same as rewinding a player.

## Using it

The compensator belongs to the game, because the game is what rewinds it. The system fills it.

```csharp compile
using Vixen.Net.Physics;
using Vixen.Net.Time;

public static class Compensation {
    public static LagCompensationSystem Wire(LagCompensator compensator, TickManager clock) =>
        new(compensator, clock);
}
```

Then tag the bodies. Nothing calls `Track` or `Forget`: the system reconciles the ring against the
world every tick, so a body that is spawned joins and one whose tag is removed leaves.

```csharp no-compile="a fragment; `world` and `pawn` are the game's own"
world.Add<LagCompensated>(pawn);
```

Where the shot is resolved, rewind to the tick the shooter claimed — clamped, because a claim is
something a client said:

```csharp no-compile="a fragment; the ray, the claim and the round trip are the game's own"
using (compensator.RewindFor(claimedTick, connection.RoundTrip)) {
    hit = physics.Raycast(from, along, range, out var result);
}
```

## Examples

**The counters say whether it is running at all.** `CapturedTicks` climbing at the tick rate is the
system doing its job; a `CapturedTicks` of zero on a live server is a system nothing scheduled.
`AdoptedCount` is one per body ever tagged and `ReleasedCount` is one per body that lost the tag or
died — the two should track your spawns and despawns, and an adopted count that climbs every tick
means something is adding the tag in a loop.

**A capture during a rewind throws, and it is not caught.** The mistake is self-reinforcing — the
ring fills with the historical poses it just installed, and what comes out is a hit-registration bug
that rots for weeks. A `RewindScope` left undisposed is a bug in the game's shot code, and the
exception points at it.

**`ClampFor` is the anti-cheat surface.** A client's claimed tick is a number a client sent, and a
modified one will send a tick a long way in the past. The clamp is `MaxRewind` plus the connection's
own measured round trip, and `LagCompensator.ClampedCount` is how often somebody asked for more than
that: a few is a player on a bad connection, and a lot from one connection is a question for whoever
reads the counter.

## See also

- [Smoothing received motion](smoothing-received-motion.md) — why the shooter was looking at the past
  in the first place.
- [Round trip and jitter](round-trip-and-jitter.md) — where the measured round trip the clamp uses
  comes from.
- [Networked players](networked-players.md) — the other end of the same problem: your own avatar is
  predicted forward rather than rewound.
