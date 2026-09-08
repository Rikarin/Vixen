---
title: Replicating a pose
slug: animation/replicating-a-pose
kind: guide
area: Networking
summary: Registering the networked-animation passes, and where the list of joints that go on the wire comes from.
api: [T:Vixen.Net.Animation.NetworkAnimationSystems, T:Vixen.Net.Animation.NetworkBoneSelectionSystem, T:Vixen.Net.Animation.NetworkBoneSelector]
tags: [networking, animation, skeleton, replication]
since: 0.1
status: preview
related: [engine/pose-precision, engine/networked-players, engine/smoothing-received-motion]
---

## What it is

**`NetworkAnimationSystems`** is the one-line registration for `Vixen.Net.Animation`: the three
replicators on a `ReplicationRegistry`, and the five passes on an `EngineLoop`.

**`NetworkBoneSelectionSystem`** is what decides which joints of a rig go on the wire, and
**`NetworkBoneSelector`** holds the two policies it can use. A `NetworkBones` record carries at most
twenty-four rotations, and a rig has far more joints than that, so something has to choose — and both
peers have to choose the same ones, in the same order, without being told.

## What it is for

Poses a receiving peer cannot reproduce from inputs: a ragdoll, IK solved against local geometry, a
hit reaction with physics in it. Everything else is cheaper as a `NetworkAnimator` — a state, a
normalised time and a speed, from which the receiving animator computes the pose itself.

You do not want it for ordinary locomotion. Twenty-four rotations at 30 Hz is about 15 kbit/s a
character; an animator state is a few dozen bits.

## Using it

Two calls, and they are deliberately separate: a registry is built once at startup and hashed into
the session's content hash, while systems belong to a loop.

```csharp compile
using Vixen.Net.Animation;
using Vixen.Net.Replication;

public static class Wiring {
    public static ReplicationRegistry Records(ReplicationRegistry registry) =>
        registry.AddNetworkAnimation();
}
```

```csharp no-compile="a fragment; `loop` is the game's own EngineLoop"
loop.AddNetworkAnimation();
```

Then attach `NetworkBones` to the characters whose pose crosses. Nothing else is needed: the selection
system gives each of them a `NetworkBoneSelection` the first time it sees one with an animator.

A game that wants to choose the joints itself sets the policy, or writes the component by hand — a
selection that is already filled is never overwritten.

```csharp compile
using Vixen.Net.Animation;

public static class Ragdoll {
    public static NetworkBoneSelectionSystem Driven() =>
        new() {
            Policy = skeleton => NetworkBoneSelector.Named(
                skeleton,
                ["Hips", "Spine", "Chest", "Head", "LeftArm", "RightArm", "LeftLeg", "RightLeg"]
            )
        };
}
```

## Examples

**Root-first is not decoration.** `NetworkBonePrecision` is indexed by *slot*, not by joint, so a
narrowed table only means anything if slot 0 is the same kind of joint on every rig in the game.
`NetworkBoneSelector.Trunk` walks breadth-first from the root, which puts the pelvis and the spine in
the low slots on any rig — and ⚠ **breadth-first rather than depth-first matters at the cap**: depth
first spends the whole budget walking one arm to the fingertips and never reaches the other, which
looks like a broken animation clip rather than like a selection.

**The policy must be a pure function of the rig.** The selection is not replicated, by design, because
it comes from content both ends already have. A policy that read anything else — a distance, a camera,
the local player's identity — would leave the two ends unpacking one wire layout into different
joints, and what that looks like is a character folded inside out.

**`WaitingCount` climbing means a rig that never loads.** An `AnimatorComponent` is null until the
content arrives, so a character is looked at again next frame and counted meanwhile. Left uncounted, a
rig that never arrives is indistinguishable from a game that never scheduled the system — and both
look like a character standing in its bind pose.

## See also

- [Pose precision](../engine/pose-precision.md) — the per-slot bit table, and why it is the
  replicator's rather than the entity's.
- [Smoothing received motion](../engine/smoothing-received-motion.md) — the transform half of the same
  problem.
