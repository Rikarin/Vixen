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
joint, loaded with `ProxyShapeCache.Get` and baked once per rig. The editor for one has a viewport of
its own — the body and its shapes drawn, and the selected shape moved, turned and resized with the
same gizmo an entity uses.

```yaml
name: hero
rig: Assets/Bodies/Hero.gltf
vocabulary: Assets/humanoid.vxshapevocab
class: humanoid
```

⚠ **`rig:` is an authoring-time reference and the bake ignores it.** A set is worn by whichever body
loads it — that is the whole reason a shape names its joint instead of indexing it — but the *editor*
cannot pose one without a skeleton, and a file that does not say which model it was drawn against
leaves every panel showing it guessing. Filling it in is what makes the viewport, the coarse
generator and the audit work; the row is at the top of the panel, so a new set's first message is
which reference to fill in.

A `.vxshapevocab` declares the names a project's sets may use, which turns "somebody called it
`palm-l` on this body and `left-palm` on that one" from a bug nobody can see into an import error. It
has an editor of its own: names, tags and body plans in one list, with a class member that names an
undeclared shape marked as you type it rather than at the next build.

## Proposing contacts

**Propose Contacts** in the clip editor plays the clip against the scene it was marked up in — the
`authoringContext:` a `.vxanim` names — and lists the contacts it noticed, most confident first.
Nothing is applied: each row has its own Add, and there is no accept-all, because the failure mode of
proximity heuristics is confident nonsense and accept-all is the button everybody presses.

What it watches is **the body's own proxy shapes**. Nothing else in a project says which joints reach
for things, and a shape is a named point somebody cared enough to write down — a palm, a fingertip, a
heel — so adding one to the set adds it to the pass. Two consequences worth knowing:

- **A shape on the root is not watched.** Once the scene has been folded in, the root carries every
  prop the scene put there; a mug looking for contacts with the character is the pass run backwards.
- **The chain root is two joints above the effector.** Wrist, elbow, shoulder is the two-bone chain
  the solver is written for. The immediate parent would propose constraints that can only bend one
  joint; the skeleton root would propose ones that move the whole body to reach a mug.

So the button needs three files to be in place: the clip names a sequence, the sequence names a
subject whose asset is the model, and some `.vxproxyshapes` names that same model in its `rig:`. Miss
any one and it says which — it will not guess a body.

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

## Joint limits

A rig may say how far each joint turns from where it was modelled — a swing cone and a twist range,
about one axis:

```csharp
built[elbow] = new SkeletonJoint {
    Name = "lowerarm_r",
    Parent = upperArm,
    InverseBindPose = inverse,
    Limited = true,
    Swing = 8f,       // degrees off the bind direction: an elbow barely leaves its plane
    Twist = 80f,      // and turns a long way about its own length
};
```

The arbiter clamps every joint on a chain back inside its limit after the solve, and the residual
goes up to say what that cost.

⚠ **`Limited` is a flag rather than "a zero limit means free".** Zero swing and zero twist is a joint
welded to its bind pose — a legitimate thing to author, and also exactly what every rig exported
before the fields existed deserialises to. Without the flag, adding them would have frozen every
character in every project.

⚠ **The clamp cuts and does not redistribute.** A solver that knew about limits could bend more at
the elbow because the shoulder ran out; this takes the correction the solver produced and removes the
parts a joint may not do. What comes out is a pose that is *legal* and further from the goal. That is
the same limitation the arbiter states above, and a staged arbiter is what fixes it.

⚠ **A rig with no limits pays one boolean.** `Skeleton.HasLimits` is checked before anything else, so
the swing–twist decomposition never runs on the rigs — almost all of them — that declare none.

## Seeing it fail

`ConstraintGizmos` draws what the last solve did: the effector, the resolved frame, the chain the
solver was allowed to move, the proxy shape a surface goal is anchored to, and — the one that matters
— a line from where the effector ended up to where it was wanted, graded green to red.

```csharp
var gizmos = loop.AddConstraintGizmos(debugDraw);

gizmos.Enabled = true;
gizmos.Only = character;      // one at a time, or a scene of thirty is a thousand lines
```

`AddAnimation` registers the evaluation and skinning passes in the same one line physics has had all
along; `EngineLoop` cannot include them in its default set, because the dependency only runs one way
and the engine has no name for an animator.

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
