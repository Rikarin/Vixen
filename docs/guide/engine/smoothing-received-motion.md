---
title: Smoothing received motion
slug: engine/smoothing-received-motion
kind: guide
area: Networking
summary: Drawing a networked object where it was rather than where the last packet said it is, and who owns the buffer that decides.
api: [T:Vixen.Net.Engine.NetworkTransformInterpolateSystem]
tags: [networking, interpolation, transforms, motion]
since: 0.1
status: preview
related: [engine/networked-players, engine/parent-relative-transforms, engine/round-trip-and-jitter, engine/pose-precision, engine/lag-compensation]
---

## What it is

A **`NetworkTransformInterpolateSystem`** keeps a `SnapshotBuffer` per `NetworkId` and, once a frame,
writes each object's `LocalTransform` from that buffer sampled at the clock's *interpolation tick* —
which is deliberately behind the tick the server is on, so that two snapshots usually straddle it.

It sits beside `NetworkTransformApplySystem` rather than replacing it. The apply system resolves
replicated frames, reparents riders onto vehicles, and places everything; this overwrites the
placement for the objects it holds a buffer for.

## What it is for

Anything a player looks at. A snapshot rate is 20 or 30 Hz and a display is 60 or 144, so the raw
received pose moves in visible steps: three still frames and a jump, forever. Interpolation trades a
fixed delay — tens of milliseconds, derived from measured jitter — for motion that is continuous.

You do not want it for a peer that reads transforms rather than draws them: a headless client, a
listen server's own copy, a bot, an editor preview, a test harness. Those want the newest value and
no delay, which is what the apply system alone already gives them. You also do not want it on the
object the local player *controls* — that one is predicted forward rather than interpolated back, and
`PredictedPlayerMovement` owns it.

## Using it

Both seams are constructor arguments, because either one missing would leave the system with nothing
it can do: no tick to key a sample by, and no moment to sample at.

```csharp compile
using Vixen.Net.Engine;
using Vixen.Net.Replication;
using Vixen.Net.Time;

public static class Smoothing {
    public static NetworkTransformInterpolateSystem Wire(ReplicationClient client, TickManager clock) =>
        new(client, clock);
}
```

Add it after the apply system. Ordering is declared on the type, so a scheduler that reads
`[UpdateAfter]` needs nothing said; a game driving its systems by hand calls them in that order.

```csharp no-compile="a fragment; `loop` is the game's own EngineLoop and `session` its NetworkSession"
loop.Add(new NetworkTransformApplySystem { Client = replication });
loop.Add(new NetworkTransformInterpolateSystem(replication, session.Clock));
```

The buffer's behaviour when it runs out is a `SnapshotBufferOptions`, passed through:

```csharp compile
using Vixen.Net.Engine;
using Vixen.Net.Motion;
using Vixen.Net.Replication;
using Vixen.Net.Time;

public static class Patient {
    public static NetworkTransformInterpolateSystem Wire(ReplicationClient client, TickManager clock) =>
        new(client, clock, new SnapshotBufferOptions { MaxExtrapolationTicks = 2 }, capacity: 64);
}
```

## Examples

**Reading the counters.** `SampledCount` is how many placements came out of a buffer and
`BufferedCount` is how many objects have one. `EvictedCount` is the other end of that: leaving
interest and being destroyed are the same thing to a client, so an object that walked over the
horizon is forgotten here, and a buffered count that only grows is a leak of a ring per object ever
seen.

**`StarvedCount` is not supposed to be zero.** An object is starved for its first few ticks, which is
one burst per spawn. A number that climbs with the match means the interpolation delay is shorter
than the connection's jitter — read `TickManager.InterpolationDelayTicks` beside it, which is derived
from the measured jitter and is the number that should have grown.

**`UnresolvedFrameCount` climbing means a rider without a vehicle.** An entity whose `NetworkParent`
has not arrived is left exactly where the apply system put it. The buffer holds what
`NetworkTransform` said, and for a parented entity that is a seat offset — a metre and a half up and
half a metre back — so interpolating two offsets and writing the result as a world position would put
the rider at the middle of the map for as long as the vehicle takes to appear.

## See also

- [Parent-relative transforms](parent-relative-transforms.md) — what a frame is, and why an
  unresolved one is held rather than guessed.
- [Round trip and jitter](round-trip-and-jitter.md) — where the interpolation delay comes from.
- [Networked players](networked-players.md) — the other half of motion: the object you control is
  predicted forward, not interpolated back.
- [Lag compensation](lag-compensation.md) — what the server does about the delay this deliberately
  introduces.
