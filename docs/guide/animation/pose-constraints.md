---
title: Pose constraints
slug: animation/pose-constraints
kind: guide
area: Animation
summary: Telling a pose where to be — four goal kinds, resolvable frames, proxy shapes, and one authored contact that fits any body.
api: [T:Vixen.Animation.Constraints.ConstraintStack, T:Vixen.Animation.Constraints.ConstraintGoal, T:Vixen.Animation.Constraints.IConstraintFrame, T:Vixen.Animation.Constraints.IConstraintArbiter, T:Vixen.Animation.Constraints.ProxyShapeSet, T:Vixen.Animation.Constraints.SurfaceFrame, T:Vixen.Animation.Constraints.ConstraintTagRecord, T:Vixen.Animation.Constraints.ConstraintGizmos]
tags: [animation, constraints, ik, contacts, proxy-shapes]
since: 0.1
status: stable
related: [animation/move-sets, animation/variation-harness]
---

## What it is

A **constraint stack** is an `IPoseProcessor`: it runs after the layers have mixed and before
skinning, and it corrects the pose towards a set of goals. There are four kinds of goal and there is
not a fifth:

| Kind | Asks for | Measured in |
|---|---|---|
| Position | a joint, or a point on one, is at a place | metres |
| Orientation | a joint's rotation aligns with one | radians |
| Aim | an axis on a joint points at something | radians |
| Distance | two joints are a certain distance apart | metres |

Each has a **region** form — anywhere on this shelf, within five degrees — and an **additive** form,
an offset from wherever the animation put you. Absolute goals average; additive goals sum on top.
Averaging two recoils would make them weaker than one, which is the opposite of what additive means.

## What it is for

Anything where the pose has to agree with the world: a hand on a rail, a foot on a slope, a chin
clear of a shoulder, two hands on a rifle, a gun pointed where the player is aiming. The claim doc 34
makes is narrower and more useful than "IK": **one authored contact, played on bodies of visibly
different proportions, lands on the same spot on all of them.**

You do not want it for the shape of a motion. A constraint corrects a pose; it does not author one.

## Where a goal is

A goal is expressed in an `IConstraintFrame`, and resolving is allowed to fail — a prop that has not
spawned, a socket nothing is bound to. Failing eases the goal out rather than snapping it.

| Frame | Where |
|---|---|
| `WorldFrame` | a fixed place |
| `JointFrame` | one of the character's own joints |
| `EntityFrame` | whatever is bound to a named slot |
| `SocketFrame` | a named attachment point on whatever is in a slot |
| `ProvidedFrame` | whatever the game wrote this frame — a raycast, a navmesh query |
| `SurfaceFrame` | a place on the surface of one of the character's own proxy shapes |
| `AttachmentFrame` | one of the character's own sockets, after it has been adapted |
| `TrajectoryFrame` | any of the above, moving over the clip |
| `ScreenFrame` | where a camera would have to be for a subject to land there in the picture |

## Proxy shapes, and why a contact is portable

A `SurfaceFrame` names a shape and a **normalised coordinate on it** — a fraction round and along,
not a distance. That is what makes one authored contact fit any body: the coordinate resolves against
whatever size that character's belly actually is.

```yaml
constraints:
  - name: right hand
    kind: Position
    effector: hand_r
    chain: upperarm_r
    begin: 0.2
    end: 0.8
    easeIn: 0.05
    easeOut: 0.1
    priority: contact
    goal:
      kind: Surface
      shape: belly
      u: 0.25
      v: 0.6
```

The shapes themselves are a `.vxproxyshapes` beside the model: seven primitives, each parented to a
joint. A `.vxshapevocab` declares the names a project's sets may use, which turns "somebody called it
`palm-l` on this body and `left-palm` on that one" from a bug nobody can see into an import error.

## Priority is a name

`priority: contact`, not `priority: 400`. A raw integer has no meaning across a project, and two
authors will pick 70 and 700 for the same intent. A `.vxpriorities` declares the ladder:

```yaml
name: default
step: 100
rungs:
  - { name: flourish,    value: 0,   meaning: A secondary motion. }
  - { name: look,        value: 100 }
  - { name: aim,         value: 200 }
  - { name: balance,     value: 300 }
  - { name: interaction, value: 400 }
  - { name: contact,     value: 500, meaning: A hand that must not slide. }
```

`contact+1` is a legal sub-step, clamped to ±99 — which is why the importer warns when two rungs are
less than a hundred apart.

## How conflicts resolve

Goals are grouped by the **chain** they move, because two goals that move the same joints have to
agree with each other and two that do not never meet. Within a group the shipped arbiter averages the
absolute goals by weight, sums the additive ones on top, and satisfies position → orientation → aim.

Priority is a share taken off the top rather than a hard ordering, so a priority that fades in causes
no jump. **A satisfied region goal takes no share at all** — "satisfied anywhere inside" has to mean
*silent* anywhere inside, or a bound is a pin.

Three things the default does not do, stated plainly because you will hit all three: it does not
guarantee a high-priority goal is satisfied exactly when a low-priority one conflicts, it does not
redistribute error towards the root, and it does not resolve two hands each targeting the other.
Those need a staged solve, which is what `IConstraintArbiter` is for.

## Seeing it fail

`ConstraintGizmos` draws what the last solve did: the effector, the resolved frame, the chain the
solver was allowed to move, the proxy shape a surface goal is anchored to, and — the one that matters
— a line from where the effector ended up to where it was wanted, graded green to red.

```csharp
var gizmos = new ConstraintGizmoSystem(debugDraw) { Enabled = true, Only = character };
```

It reads `ConstraintStack.LastSolved` rather than re-resolving, so what is drawn is what happened. A
goal that never resolved is drawn grey and labelled `unresolved`, because a residual of zero says
both "landed" and "never ran" and those mean opposite things.

## Cost

The stage is off until something uses it: with no goals, `Solve` returns before it computes anything.
Measured on a hundred characters, a build with the stage installed and no goals is indistinguishable
from one without it; two solved goals each costs about 380 µs more.

When that is too much, `ConstraintGovernor` has three knobs — rate, detail and scope — and reports
what it dropped. It reserves the floor for every character still waiting rather than spending the
budget on the most important ones in order, which is the difference between a hundred characters at
one-in-four and thirty-seven characters at full rate.

## Seams

| Seam | The default | What else fits |
|---|---|---|
| `IConstraintFrame` | nine shipped | a physics query, a navmesh, a spline, another system's spatial data |
| `IBindingSource` | a transform and its sockets | a table of participants resolved per interaction |
| `IConstraintArbiter` | one weighted pass | a staged solve, error redistribution, exact satisfaction of a layer |
| `IConstraintScheduler` | plans nothing | dependency discovery between characters, simultaneous multi-character solves |
| `IChainSolver` | two-bone analytic | an iterative solver for a long chain, a data-driven one |
| `IProxyShapePoser` | a joint's transform | a shape sized by a morph weight or a simulation |
| `IClipMetadataExtension` | timed notes | a project's own tag kinds, checked at import and carried untouched |

Every one has a second implementation, and `SeamTests` fails the build if one does not.

## See also

- [Move sets](animation/move-sets) — the other half of doc 34, and independent of this one
- [The variation harness](animation/variation-harness) — whether a marked-up clip actually works on every body
