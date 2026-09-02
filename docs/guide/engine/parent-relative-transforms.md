---
title: Parent-relative transforms
slug: engine/parent-relative-transforms
kind: guide
area: Networking
summary: Replicating a rider on a moving vehicle, and sending only the axes an object actually uses.
api: [T:Vixen.Net.Motion.NetworkParent, T:Vixen.Net.Motion.NetworkParentReplicator, T:Vixen.Net.Motion.NetworkTransformAxes]
tags: [networking, replication, transforms, vehicles, bandwidth]
since: 0.1
status: preview
related: [engine/networked-players, engine/networked-prefabs, gameplay/movement]
---

## What it is

A **`NetworkParent`** says which entity a `NetworkTransform` is quoted relative to. Zero — the value a
default one has — means the world, which is what everything that is not standing on something else
is.

**`NetworkTransformAxes`** is the other half: which parts of a transform a replicator bothers to send.

Both exist because a `NetworkTransform` on its own is a world position and a rotation, and a world
position is the wrong number for a passenger. A rider on a boat replicating world coordinates is
fighting the boat's own — two independent streams of quantized positions, arriving on different ticks
through different channels, describing one thing that is bolted to another.

## What it is for

A rider in a vehicle seat, a turret on a tank, a crate lashed to a ship, a player standing on a lift.
Anything whose position is best described as *"here, on that"*.

You do not want it for an object that merely happens to be near another one. A frame costs a reliable
record whenever it changes and a hierarchy the receiving peer has to maintain; an object that stands
on the ground is a root, and roots are free.

## Using it

The server parents the rider to the vehicle and moves it in the vehicle's space, exactly as it would
in a single-player game. `NetworkTransformCaptureSystem` notices the parenting and publishes the
frame; nothing else has to be said.

```csharp no-compile="a fragment; `world`, `rider` and `vehicle` are entities the game already has"
Hierarchy.SetParent(world, rider, vehicle);
world.Get<LocalTransform>(rider).Position = seat.Offset;
```

On the receiving peer, the apply system needs the map from a `NetworkId` to a local entity, which is
the client's:

```csharp compile
using Vixen.Net.Engine;
using Vixen.Net.Motion;
using Vixen.Net.Replication;

public static class Wiring {
    public static NetworkTransformApplySystem Transforms(ReplicationClient client) =>
        new() { Client = client };

    public static void Replicated(ReplicationRegistry registry) {
        registry.Register(new NetworkTransformReplicator());

        // Only if something in the game is ever parented. A registry that does not have it is a
        // game whose objects are all roots, which costs nothing and says so.
        registry.Register(new NetworkParentReplicator());
    }
}
```

⚠ **Without a `Client` every frame is unresolved**, and a parented entity is held still rather than
put somewhere wrong. That is the right way round, and it is also why
`NetworkTransformApplySystem.UnresolvedFrameCount` climbing steadily is the first thing to look at
when parented objects do not move.

## What happens when the vehicle has not arrived

⚠ **This is the case the design is mostly about.** The frame and the transform are separate records
on separate channels, and on the tick somebody mounts, the vehicle may not have been spawned on this
peer at all — interest resolved the two in one order and the budget shed them in another.

The rider is then **not placed**. Its numbers are a seat offset; read as world coordinates they would
put it a metre and a half above the world origin until the vehicle turned up, and the correction
after that reads as the netcode throwing people around. Holding it where it was costs a rider who is
briefly stale, and staleness is what the snapshot buffer is for.

It retries every tick, because the value arrived and simply could not be used — so the entity's
transform stops changing, and a pass that only looked at what changed would look once and never
again.

A handful of unresolved frames per mount is ordinary. A number that keeps climbing is a frame that
will never arrive — an interest rule sending the rider and not the vehicle — which is the failure the
counter exists to name.

## Parents the wire cannot name

An entity parented to something with no `NetworkId` — an art pivot, a spawn marker, anything the game
did not network — is published in **world space**, with no frame claimed for it. There is no honest
alternative: the other end cannot reconstruct a frame it has no name for.

⚠ **This corrects a defect rather than adding a feature.** The bridge used to publish `LocalTransform`
verbatim, so such an entity sent an offset that the receiver read as a world position — silently, and
wrong by however far the parent was from the origin.
`NetworkTransformCaptureSystem.UnnameableFrameCount` counts it, because resolving a world matrix costs
a multiply per level of depth and a game paying that on a thousand entities should give the parent an
id instead.

## Sending fewer axes

A door that only rotates pays forty-eight bits a tick for a position that has not changed since the
level loaded. Naming the axes stops that:

```csharp no-compile="a fragment; `registry` is the game's ReplicationRegistry"
registry.Register(new NetworkTransformReplicator(NetworkTransformAxes.Rotation));
```

That replicator writes forty bits rather than eighty-eight.

⚠ **The mask belongs to the replicator, not to the entity.** Both ends have to agree about a lane
layout before a single bit is decodable, and the delta codec checks a baseline against one fixed
width — so a mask that varied per entity would be a wire format that varied per entity. A game
wanting one mask for doors and another for players gives the doors a component of their own, exactly
as `NetworkTransform`'s own remarks say a game with a bigger world does.

⚠ **A narrowed replicator is a different type on the wire.** The mask is folded into the type name
and therefore into `ReplicationRegistry.ManifestHash`, so two peers built with different masks fail
the handshake instead of decoding each other's transforms into plausible wrong numbers. The unmasked
default keeps the bare name, so nothing that already ships moves.

⚠ **An axis nobody sends keeps the value the receiver already had.** A door replicating only its
rotation has its position from the prefab that built it, and a receiver that rebuilt the component
would put every door in the level at the world origin — a zeroed field whose zero is a perfectly
valid position.
