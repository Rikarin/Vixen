---
title: Pose precision
slug: engine/pose-precision
kind: guide
area: Networking
summary: Spending fewer bits on the bones of a replicated pose that nobody is looking at.
api: [T:Vixen.Net.Animation.NetworkBonePrecision]
tags: [networking, replication, animation, bandwidth, quantization]
since: 0.1
status: preview
related: [engine/parent-relative-transforms, engine/networked-players, engine/measuring-loss]
---

## What it is

A **`NetworkBonePrecision`** is how many bits each bone of a replicated pose is worth. It is handed to
a `NetworkBonesReplicator`, and it narrows the lanes that replicator writes.

A pose goes on the wire only when the receiver cannot reproduce it — a ragdoll, IK solved against
local geometry, procedural motion with a random number generator in it. That is the expensive case by
construction: twenty-four bones at 32 bits is 776 bits whole, about 15 kbit/s a character at twenty
updates a second. This is the dial that makes some of those bones cheaper.

## What it is for

**A finger does not need what a spine needs.** Error compounds down a chain, so the joint nearest the
root is the one whose precision everything below it inherits; a fingertip's own error reaches nothing
and is a centimetre at the end of the hand. Ten bits a component is the right price for a pelvis and
an extravagant one for a knuckle.

You do not want it for a pose that is already affordable. The delta codec spends **one bit** on a bone
that did not move, so a pose that is mostly still costs almost nothing at any precision — narrowing
buys something on the poses that are moving everywhere at once, which is exactly the ragdoll case this
component exists for.

## Using it

Order the selection most-important-first, then say what each slot is worth.

```csharp
// Pelvis, spine, chest, head whole; the arms at eight bits; everything past them at six.
var precision = NetworkBonePrecision.For(
    [10, 10, 10, 10, 8, 8, 8, 8, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6]
);

registry.Register(new NetworkBonesReplicator(precision));
```

`NetworkBonePrecision.Uniform(bits)` is the same width everywhere. A table shorter than the pose leaves
the rest whole: `For([6, 6])` says something about the first two slots and nothing about the others.

Widths run from `MinBits` (4) to `MaxBits` (10). Below the floor the two-bit selector is most of the
record and the pose steps visibly; a joint you want cheaper than that you want out of the selection,
which costs nothing at all.

## The slot is not the joint

⚠ The table is indexed by **position in `NetworkBoneSelection`**, not by joint index. The wire layout
is a property of the replicator and joint indices are a property of a rig, so a per-entity table would
be a wire format that varied per entity — the delta codec checks one fixed lane width and the
connection baselines are compared against one layout, and nothing on either side could parse it.

The consequence is worth planning for rather than discovering: a game using a narrowed table must
order every character's selection the same way. Most-important-first is the natural convention, and it
makes slot 0 the pelvis on every rig in the game.

## The handshake is what protects you

⚠ A narrowed table **renames the type on the wire**. `NetworkBonePrecision.Full` keeps the bare name,
so nothing that ships today changes its wire id; any other table appends its own suffix, which folds
into the replicator's `TypeId` and therefore into `ReplicationRegistry.ManifestHash`.

That matters because two peers built with different tables would disagree about every lane width in
the layout and would decode each other's poses into plausible wrong rotations — a character folding in
half rather than an error anybody can act on. With the name carrying the table, the connection is
refused at the handshake instead. It is the same argument `NetworkTransformAxes` makes, and it lands
the same way.

## What it costs

| Table | Bits a record | Against 776 |
|---|---|---|
| `Full` | 776 | — |
| four whole, four at eight, sixteen at six | 560 | 72 % |
| `Uniform(6)` | 488 | 63 % |
| `Uniform(4)` | 344 | 44 % |

Six bits over ±1/√2 is a step of about 0.022 in a component, which is a couple of degrees; four bits is
about five. Both are decisions about a limb somebody is watching from ten metres away, and neither is a
decision about where the character is — that stays `NetworkTransform`'s answer.

One number does not change: **a bone that did not move still costs one bit.** The narrowing drops bits
off the packed value in the integer domain rather than re-encoding the rotation, so two identical poses
stay bit-identical and the delta codec still charges nothing for the still half of a pose.
