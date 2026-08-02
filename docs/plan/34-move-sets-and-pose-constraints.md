<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# 34 — Move sets and pose constraints

Two additions to the animation stack, both of which exist because the state machine is the wrong
shape for the two things games actually ask an animation system to do.

**A move set** replaces the hand-built locomotion graph: a character's movement vocabulary is a
**flat catalogue of clips carrying descriptive facets**, and picking one is a **scored query** over
that catalogue rather than a walk through an authored tree. Adding "injured" or "waist-deep in
water" to a character costs a handful of clips and a facet, not a parallel graph.

**Pose constraints** replace the fixed IK triplet: a clip carries **authored spatio-temporal goals**
— put this hand *here on this surface*, aim this axis at *that*, keep this limb within *that* length
— and a solver stage enforces them against whatever body, prop and environment the game actually
has, rather than the ones the clip was authored against.

⚠️ **Extends [04](04-ecs-and-scripting.md) and [29](29-players-and-possession.md), and closes the
`.vxanim` runtime row of [`../overview.md`](../overview.md).** It is a separate file for the reason
[26](26-virtual-cameras.md) and [31](31-terrain-grass-and-trees.md) are: the first half is an
argument rather than a schedule, and two subsystems own a piece of it.

**The claim this document has to earn.** A clip authored once, against one body, in one place, plays
correctly on a body with different proportions, against a prop of a different size, on ground that is
not flat — and the game code that caused it to play knows none of that. If that claim fails, the
honest answer is what the stack already has: blend trees and three IK solvers, with an artist
authoring a variant per case.

**Read [the seams](#part-4--the-seams) before the phases.** A large part of the value here is *what
is deliberately left open*: the shipped defaults are the simple, well-trodden ones, and every place
where a project will want something more elaborate is an interface rather than a fork.

---

## The rows this touches

Four, and one of them is a debt this document has to pay before it can start.

### `Vixen.Animation` ✅ — "skeletal playback, blend trees, layers + masks, state machine, IK"

Not overturned. Everything below sits **on top of** what is there, through hooks that already exist:
a move set is a `Motion`, so a state can hold one; the constraint stage is an `IPoseProcessor`, so it
runs where `FootPlacement` runs today. Nothing in this document changes a signature in the assembly's
current surface.

### ⬜ "A runtime path for `.vxanim`"

**This is a hard prerequisite, not an adjacency.** Constraints are clip metadata. There is nowhere to
put clip metadata until an authored `.vxanim` compiles to something a build can load by address, and
that row is already owed — `.vxscene`, `.vxmat` and `.vxcompositor` have all made the move and this
one has not. P0 below is that row, done for its own sake, with the metadata sidecar designed in from
the start rather than bolted on afterwards.

### ⬜ "Ragdoll integration — lands with the animation/physics join"

Unchanged and still owed, but the constraint stage is where it lands when it does: a ragdoll blend is
a set of position goals with a weight ramp, which is exactly the shape [D10](#d10--four-goal-kinds-and-no-more)
describes. This document does not schedule it; it stops it needing a second mechanism.

### [33](33-character-creator.md) — a character is a parameter vector

The character creator's whole premise is that **proportions vary continuously at runtime**, which is
precisely the condition under which an authored clip stops being correct. Doc 33 § D13 makes control
*names* the interoperability surface for face rigs; this document does the same job for the body, and
the two are the same argument applied to two ends of the same skeleton. Neither blocks the other.

---

## The argument

### Why the locomotion graph stops scaling

A character that walks, jogs and runs, starts, stops and turns is about twenty clips and a blend
tree, and that is a solved problem — Vixen has `BlendTree1D`, `BlendTree2D` and a state machine, and
they do it well.

Then the character gets injured. Every clip in the graph now has an injured counterpart, and the
graph is duplicated. Then it is carrying something in one hand: duplicated again, or masked onto an
upper-body layer, which works until carrying changes the *gait* and not just the arms. Then it wades
into water, and the response is not a variant of walking but a set of responses indexed by depth.
Then there is a second character with the same skeleton and a different walk, which shares 90 % of
the first one's graph and cannot express that.

The failure is not that the graph is big. It is that **the graph encodes the cross-product**. Four
independent conditions with three values each is eighty-one graphs, and no amount of editor tooling
makes eighty-one graphs maintainable, because the thing an author wants to say — "when injured, the
walk is *this* clip and everything else is unchanged" — is not a sentence the structure can hold. It
can only hold "here is a whole graph for injured".

The insight is that **a character's movement vocabulary is a set, not a tree**. Each clip in it knows
what it is for: this one is a walk; this one is a walk *for an injured character*; this one is a stop
*from a run*, *on ice*. Those are independent descriptive facts, and once they are stored as
independent facts, selecting a clip is a *query* — "the best walk, given that I am injured and on
ice" — which resolves against whatever the set happens to contain. Nobody authors the cross-product.
The query degrades: with no injured-on-ice walk it finds the injured walk, and with neither it finds
the walk.

⚠ **This is the same move [08](08-asset-pipeline-and-addressables.md) made with addressable labels
and [28](28-gameplay-framework.md) made with gameplay tags**, and it is made for the same reason: a
hierarchy forces an author to pick one axis to be primary, and there is never one.

### Why the IK triplet stops being enough

Vixen has three solvers: two-bone, look-at, foot placement. Each is a good implementation of a fixed
question, and between them they cover the three cases every engine covers. They also share a
limitation that is easy to miss: **the game asks the question, every frame, in code.**

`FootPlacement` works because the game knows what a foot is and where the ground is. Nothing else in
a game world has that property. When a character puts a hand on a table, the game does not know the
hand is meant to be on the table — the animator knew, three months earlier, in a DCC tool, and then
expressed that knowledge by *baking a pose that happened to touch a table of that height*. Play it
against a table 10 cm taller and the hand is inside the wood. There is no bug to fix. The information
that the hand was supposed to be on the surface was never written down anywhere the runtime can read.

So the second half of this document is not "more IK solvers". It is a **place to write that
information down**: the clip says "between 0.3 and 0.8 of my duration, the right hand is on the
surface of *this* shape, easing in over 0.1, at this priority", and the runtime enforces it against
the shape that is actually there. The solver matters much less than the fact that the goal exists at
all.

⚠ **The expensive part of this is authoring, not solving**, and [the risks](#risks) says so plainly.
A constraint system with nothing marked up does nothing.

---

## What the references actually ship

### Unity — Mecanim, and Animation Rigging as a separate package

Mecanim is the state-machine-and-blend-tree design Vixen already has, including the
sub-state-machine nesting that is the cross-product problem in its purest form. What is worth taking
is **the humanoid avatar**: a named-bone abstraction that lets a clip authored on one rig play on
another, which is `RetargetMap` in Vixen and already built.

Animation Rigging, shipped separately and much later, is the one to study. Its model is: a stack of
**rig layers** on the animator, each holding **constraints** (`TwoBoneIKConstraint`,
`MultiAimConstraint`, `MultiPositionConstraint`, `MultiParentConstraint`, `ChainIKConstraint`), each
with a weight, each pointing at scene transforms as sources and targets. It solves top to bottom in
authored order.

**What it gets right**: constraints are data, they have weights, they compose, and the four kinds it
settles on are the right four. **What it does not do**: a constraint's target is a *scene transform*
that something else has to place, so the "where on the surface" problem is pushed back to game code;
constraints live on the rig, not on the clip, so they cannot be authored per-clip or scheduled
against clip time; and arbitration is authored order, so two constraints wanting the same bone is
resolved by whoever is later.

### Unreal — Control Rig, IK Rig, and the Chooser

Three separate systems, and the split is instructive.

**Control Rig** is a full node-graph evaluated per frame — forward solve, backward solve, Full-Body
IK. It is extremely general and correspondingly expensive to author and to run; it is a rigging
language, not a constraint system.

**IK Rig / IK Retargeter** is the closest published thing to the second half of this document: named
**retarget chains** (spine, left arm, …), **goals** with position and rotation blending, and a
full-body IK pass that preserves goals while the underlying proportions change. It is explicitly
built for "the same animation on a differently proportioned character".

**The Chooser / Pose Search (Motion Matching)** is the closest published thing to the first half. A
Chooser table is rows of clips with columns of conditions, evaluated to a set of candidates; Pose
Search then picks among candidates by matching a *feature vector* of the current pose and trajectory
against a database. The direction of travel across the industry is clear: **from authored graphs
towards queried databases**, for exactly the cross-product reason above.

### Ubisoft's IK Rig, and the research line behind it

The published IK Rig work (Bereznyak, GDC 2016) is the canonical statement that retargeting should be
expressed as **procedural constraints over a simplified body model** rather than as bone-to-bone
transfer, and it is the direct ancestor of what Unreal shipped. Behind it sits a long public line —
Gleicher's *Retargetting Motion to New Characters* (SIGGRAPH 1998), Shin et al.'s importance-based
online retargeting (2001), and the spacetime-constraints literature — all of which establish
constraint-based pose adaptation as ordinary, published technique.

Motion matching's public line is the same: Büttner and Clavet's original talk (GDC 2015/2016), then
*Learned Motion Matching* (Holden et al., SIGGRAPH 2020).

⚠ **Vixen takes the published shapes.** Where this document specifies something, it is because one
of the references above already established it or because the alternative was measurably worse for
Vixen's constraints, and each decision below says which.

### The consensus

| | Everyone ships | Vixen's position |
|---|---|---|
| Blend trees, layers, masks, a state machine | Yes | ✅ Built |
| Named-bone retarget between rigs | Yes | ✅ `RetargetMap` |
| Two-bone / aim / chain IK | Yes | ✅ Three solvers |
| Constraints as *data*, with weights, that compose | Unity (rig), Unreal (IK Rig) | ⬜ **This document** |
| Constraints authored **on the clip**, over clip time | Rarely, and never well | ⬜ **This document** |
| A goal expressed **on a surface**, not at a point | No published engine | ⬜ **This document** |
| Clip selection by **query** rather than graph walk | Unreal (Chooser + Pose Search) | ⬜ **This document** |
| Full motion matching against a pose database | Unreal, some in-house | ❌ Out of scope — see [D8](#d8--the-selector-is-a-motion-and-motion-matching-is-not-in-it) |

---

## Where Vixen already is

Better than the gap suggests. `Vixen.Animation` is 32 files and ~5.7 kLOC, benchmarked, and the
pieces this document needs are load-bearing already.

| What exists | Why it matters here |
|---|---|
| `Motion` — one abstraction for "a clip or a tree of them", returning pose, root motion and events from one `Evaluate` | A move set is a `Motion`. Everything above it — states, layers, masks, root motion, events — needs no change whatsoever |
| `IPoseProcessor` — a hook after the layer mix, before skinning, with a model-space scratch buffer | The constraint stage is one of these. The hook's doc comment already argues for exactly this position in the frame |
| `AnimationParameters` — named-to-declare, indexed-to-read, with a four-case value union | Selection inputs are parameters. No second state store is needed, and [D4](#d4--selection-inputs-are-parameters-and-there-is-no-second-store) argues that at length |
| `Skeleton` — immutable, shared, parents-precede-children, frozen name map | Every pass below is a single forward loop over joints. The invariant is checked at load, not assumed |
| `PoseScratch` with a `ref struct` lease | A solver needs two or three temporary poses and must not allocate |
| `TwoBoneIk`, `LookAtIk`, `FootPlacement` | Not replaced. They become the *chain solvers* the constraint stage dispatches to, and `FootPlacement` becomes a preset over two position goals |
| `SkeletonRetarget` + `RetargetMap` | Solves the *skeleton* mismatch. This document solves the *world* mismatch. They compose: retarget first, constrain after |
| `AnimationCurveCompressor` with a report | The compression story for goal curves is already written; [D15](#d15--a-goal-may-be-a-curve-over-clip-phase) reuses the shape |
| `Vixen.Editor.AnimationGraph` — an authored graph as a document plus a compiler | The pattern every authoring surface below follows: a serialisable document, and a compiler that produces the runtime object |

**The gap is narrow and specific.** There is no place to put per-clip metadata, no notion of a volume
attached to a bone, no arbitration between two things wanting the same joint, and no continuity state
so that a goal appearing mid-blend eases in rather than snapping. Everything else is scaffolding that
already holds.

---

## Part 1 — decisions

### D0 — No new assembly, two new namespaces

`Vixen.Animation.Moves` and `Vixen.Animation.Constraints`, inside the existing assembly. Both depend
on `Skeleton`, `BoneTransform`, `SkeletonPose`, `PoseScratch` and `Motion`; a separate assembly would
either duplicate those or widen their surface across a boundary, and would buy nothing — nobody wants
constraints without animation. Authoring goes where authoring already lives:
`Vixen.Editor.AssetEditors` (the clip editor exists) and `Vixen.Editor.AnimationGraph`.

⚠ **The one place this is revisited** is if the constraint solver grows a dependency on
`Vixen.Physics` for shape queries. It must not: [D12](#d12--proxy-shapes-are-bone-attached-primitives-with-names-and-tags)
keeps proxy shapes as pure geometry with their own intersection code, and a game that wants a
physics-driven goal supplies it through a frame provider ([D11](#d11--a-goal-is-expressed-in-a-frame-and-a-frame-is-resolvable)).

---

### D1 — A move is a clip plus facets; there is no style container

```
MoveEntry
  MoveKey       key          // stable, hashable identity: what this move IS
  Motion        motion       // a ClipMotion, or a blend tree — reuses the existing abstraction
  FacetSet      facets       // interned symbol pairs: gait=walk, condition=injured, surface=ice
  MoveTraits    traits       // numeric: speed range, turn rate, arc, foot phase at start, duration
```

A **`MoveSet` is a flat array of `MoveEntry`** plus indices built at bake time. There is no container
per style, no container per character, and no containment relationship between moves. A move's
"style" is a facet on the move, the same as every other descriptive fact about it, and it has no more
structural standing than "this one turns left".

**Why flat.** The moment a style is a container, every question that crosses two axes needs a nesting
order, and there is no correct one — is the injured-in-snow walk inside `injured` or inside `snow`?
Facets have no order, so the question does not arise. It also makes the runtime representation a
`ReadOnlySpan<MoveEntry>` over one allocation, which matters more than it sounds: selection touches
every candidate.

**Facets are interned.** A `Facet` is a pair of 32-bit symbol ids (`key`, `value`), a `FacetSet` is a
sorted run of them, and matching is integer comparison over sorted runs. No strings and no hashing in
a frame. Interning happens at bake, and the symbol table ships with the set.

### D2 — Selection is a scored query, not a lookup

```
MoveQuery
  FacetSet          required     // hard filter — a candidate lacking any of these is out
  ReadOnlySpan<WeightedFacet> preferred   // soft — each contributes its weight when matched
  MoveTraitTargets  numeric      // desired speed, heading delta, turn radius, …
  MoveKey           previous     // what is playing, for the transition rules of D5
```

Scoring, in one pass over the candidates:

1. **Filter.** Drop anything missing a required facet, anything whose traits cannot reach the
   numeric targets, and anything the transition rules forbid from `previous`.
2. **Score.** `Σ weight over matched preferred facets` + `numeric proximity` (normalised, so a speed
   error and a heading error are comparable) − `a small penalty for repeating the move just played`,
   which is what stops a two-candidate set alternating visibly.
3. **Pick.** Highest score. Ties broken by `MoveKey` ordinal — **deterministically**, because
   [16](16-networking.md) means two machines must pick the same move from the same inputs.

**Why scoring rather than descent.** Descent has to be told which axis to branch on first, and
answers "no such node" with a fallback rule. Scoring answers "the closest thing I have" natively,
which is the behaviour every fallback rule is trying to approximate. It also means adding a facet to
the vocabulary costs nothing structural: unqueried facets simply never contribute.

**Cost.** A filter-and-score pass over a few hundred entries, on the frames where the query changes.
The query is only re-run when its inputs change or the current move is near its end — which
[D3](#d3--move-sets-compose-by-overlay-and-the-overlay-happens-at-bake) makes cheap to detect, since
a `MoveQuery` is a value type and comparing two is comparing a handful of words. Budget: **under
5 µs for a 500-entry set**, measured, with a `Vixen.Benchmarks.Animation` case.

### D3 — Move sets compose by overlay, and the overlay happens at bake

A `.vxmoves` asset may declare a list of **base sets** it overlays. Overlay is set union keyed by
`MoveKey`, later wins:

```
Human            → walk, run, turn_l, turn_r, stop, …        (200 entries)
Human + Guard    → overrides walk, run; adds patrol_scan     (3 entries authored)
Human + Guard + Captain → overrides walk                     (1 entry authored)
```

**The composition is resolved by the asset compiler, not at runtime.** What a build loads is one
flat baked table with the overlays already applied and the symbol table already interned. There is no
inheritance chain in memory, no fallback walk at selection time, and no way for a runtime bug to
resolve an override differently from the editor preview.

⚠ **Overlay is a list, not a parent pointer**, so a set can overlay two unrelated bases (a body-type
set and a personality set) without either knowing about the other, and the diamond has a defined
answer — later in the list wins, and the compiler reports every key that more than one base
supplied.

### D4 — Selection inputs are parameters, and there is no second store

The values a query is built from — `injured`, `surface`, `carrying`, `stance` — are
`AnimationParameters`, set by game code exactly as `Speed` is set today.

One new parameter type: **`Symbol`**, holding an interned 32-bit id, so `parameters.SetSymbol("surface", "ice")`
stores an integer and every comparison against it stays an integer comparison. It fits the existing
four-case union with no size change.

**Why not a separate loosely-coupled state bag.** It is genuinely tempting: a general key/value store
that any system can write to without knowing about animation is a clean-looking decoupling. It is
also a second thing to serialise, a second thing to replicate, a second thing to inspect in a
debugger, a second place for a name to be misspelled with no error, and — worst — a second answer to
"what is this character's state right now" that can disagree with the first. Vixen already has one
answer, it is already indexed, already replicated by `Vixen.Net.Animation`, already inspectable, and
already the thing conditions read. Adding a facet channel to it costs one enum case.

⚠ **The decoupling the second store was for is real, and it is solved elsewhere.** Game code that
must not know about animation writes gameplay tags ([28](28-gameplay-framework.md)); a small adapter
projects the tags it cares about onto animation parameters. That is one explicit mapping in one place
instead of an implicit coupling through shared string keys.

### D5 — Transitions are rules over facets, not a table of pairs

```
TransitionRule
  FacetPredicate from, to      // matches on facets, wildcards allowed
  float          duration
  EasingCurve    curve
  BoneMask?      mask
  SyncMode       sync          // None | Phase | ClosestFoot
  bool           allowed
```

Rules are an ordered list; **first match wins**, and the last rule is the default. A pairwise table
over N moves is N² cells that nobody fills in; a rule list is a dozen sentences an author can read:

```
run → *          : 0.20s, phase-synced
*   → stop_*     : 0.12s
injured:* → *    : 0.35s                  # everything an injured character does starts slowly
*   → *          : 0.25s                  # default
```

This is the same first-match-wins shape as VCSS selectors in [09](09-ui-framework.md), and the
authoring tool shows which rule matched for a chosen pair, the way a style inspector does.

### D6 — Phase sync is a first-class transition mode

A `MoveEntry` carries **foot phase**: where in normalised time each foot plants. `SyncMode.Phase`
starts the incoming move at the phase that continues the outgoing one, and `ClosestFoot` picks the
phase whose contact matches the foot currently down.

This exists because `MotionContext` is already normalised time and the doc comment on it already
makes the argument for why. Sync is the same argument applied across a transition rather than within
a blend, and without it every gait change visibly skates.

### D7 — Intent comes from `MoveIntent`; the selector never reads input

The numeric half of a query — desired speed, heading, turn radius — is derived from
[29](29-players-and-possession.md)'s `MoveIntent`, which is already the one seam between input,
physics and the wire. A per-body **`IGaitModel`** does the derivation:

```
interface IGaitModel {
    void Describe(in MoveIntent intent, in MoveState state, ref MoveTraitTargets targets);
}
```

A biped, a quadruped and something with wheels differ here and nowhere else. Shipping: a biped model.
The interface exists so the second one is not a fork.

⚠ **This keeps the whole selection layer testable with a `for` loop**, which is the property
`Animator`'s doc comment already claims for the assembly and which this must not break.

### D8 — The selector is a `Motion`, and motion matching is not in it

```
sealed class MoveSetMotion : Motion
```

It holds a `MoveSet`, a `IGaitModel`, the transition rules, and the currently-playing entry's
`Motion`. It *is* a `Motion`, so an `AnimationState` holds one and every layer, mask, event and
root-motion path above it works with no change. A game can also use one directly, with no state
machine at all, which is the common case for an NPC.

**Motion matching — a per-frame nearest-neighbour search over a pose-and-trajectory feature database
— is explicitly out of scope.** It is the better technique for a large budget and it is a different
project: it needs a feature-extraction bake, a searchable database format, an acceleration structure,
and considerably more animation data than a move set does. The relevant point is that **it fits
here**: a motion matcher is a `Motion` that ignores facets, and the seam it would need
([`IMoveSelector`](#part-4--the-seams)) is defined in P1 whether or not anyone implements one.

---

### D9 — Constraints are a pose stage, and the stage is `IPoseProcessor`

```
sealed class ConstraintStack : IPoseProcessor
```

Nothing new is needed to place it: `IPoseProcessor.Process` already runs after the layer mix and
before skinning, with a model-space scratch buffer, and its doc comment already argues that this is
where corrections belong. The existing three solvers keep working unchanged beside it, in the order
the list gives.

### D10 — Four goal kinds, and no more

| Kind | The effector | The goal | Satisfied when |
|---|---|---|---|
| **Position** | a joint, or an offset from one | a point in a frame | the effector is at the point |
| **Orientation** | a joint's rotation | a rotation in a frame | they align |
| **Aim** | an axis on a joint, from an origin | a point in a frame | the axis points at it |
| **Distance** | two joints | a `[min, max]` interval | the separation is inside it |

These are the four Animation Rigging settles on and the four the retargeting literature uses, and the
fifth candidate is always a composite of them. Each also has a **region** form — the goal is a volume
or an angular cone rather than a point, satisfied anywhere inside — which costs one branch in the
error function and buys the difference between "the hand is exactly here" and "the hand is somewhere
on this shelf", which is what most authored intent actually is.

Each goal carries: `weight ∈ [0,1]`, `priority` (an integer; [D16](#d16--arbitration-is-one-weighted-pass-by-default-and-the-policy-is-an-interface)
says what it does), the **chain** it may move (first joint, effector joint), and a `label` — a symbol
other systems query and can suppress.

⚠ **`FootPlacement` becomes a preset**: two position goals with a region of the ground plane's
tolerance, plus an orientation goal per foot. It stays as its own class because a game that wants
only feet should not have to meet any of this — but it is reimplemented over the stage so there is
one arbitration story rather than two.

### D11 — A goal is expressed in a *frame*, and a frame is resolvable

```
interface IConstraintFrame {
    bool TryResolve(in ConstraintContext context, out Frame frame);   // transform + scale
}
```

Shipping implementations:

| Frame | Resolves to |
|---|---|
| `WorldFrame` | a fixed world transform |
| `EntityFrame` | a bound entity's transform |
| `JointFrame` | a joint on a bound skeleton, plus a local offset |
| `SocketFrame` | a named socket (an attachment point on a skeleton) |
| **`SurfaceFrame`** | a point *on the surface* of a named proxy shape, in normalised surface coordinates — [D13](#d13--normalised-surface-coordinates-are-what-makes-a-contact-portable) |
| `ProvidedFrame` | whatever the game wrote this frame, by name |

**`TryResolve` returning false is the important case.** A frame that names a bound entity which no
longer exists, or a shape that is not loaded, fails cleanly — and
[D17](#d17--temporal-continuity-belongs-to-the-solver) eases the constraint out instead of snapping
the limb. Resolution failure is expected, not exceptional.

**Binding.** Which entity is "the thing I am interacting with" is a named slot on the animator:

```
bindings.Set("ground", groundProvider);
bindings.Set("held-item", swordEntity);
bindings.Set("look-target", playerEntity);
```

A clip's constraints refer to slots by name, so the same clip works against whatever is bound. This
is the same pattern as a UI data context in [09](09-ui-framework.md), and it is the principal seam
for anything multi-party — see [Part 4](#part-4--the-seams).

### D12 — Proxy shapes are bone-attached primitives with names and tags

```
ProxyShape
  Symbol      name          // "belly", "left-palm", "seat"
  ShapeKind   kind          // Box | TaperedBox | Sphere | Capsule | TaperedCapsule | Cylinder | Cone
  int         joint
  Transform   localOffset
  ShapeParams dimensions
  FacetSet    tags          // "grip-surface", "seat", "soft"
```

A `ProxyShapeSet` is an asset, authored against a skeleton, and referenced by a `SkinnedRenderer` the
way a material is.

**Why not physics colliders.** Three reasons, and they are all load-bearing:

1. **Fidelity.** A physics body wants the cheapest shape that stops interpenetration. A contact wants
   the shape that describes the *surface a hand lands on*, which is a different and usually finer
   thing — a forearm is one capsule to physics and three to a sleeve rolled up.
2. **Cost.** A character may have a hundred of these, and posing all of them every frame to serve two
   active constraints is waste. **They are posed lazily**: the stage walks the active goals, collects
   the shapes their frames name, and poses only those. A frame typically touches two to six.
3. **Coupling.** [D0](#d0--no-new-assembly-two-new-namespaces) keeps `Vixen.Animation` off
   `Vixen.Physics`, and this is where that would break.

**Tags, not a type hierarchy.** A shape is described by what it affords — `grip-surface`, `seat`,
`mountable` — and a constraint may name a shape by tag rather than by name, which is what makes one
authored sitting clip work against a chair, a bench and a crate. Same reasoning as
[D1](#d1--a-move-is-a-clip-plus-facets-there-is-no-style-container); same interning.

**Two detail levels.** A set may declare a coarse subset. [D20](#d20--lod-has-three-knobs-and-they-are-independent)
says when each is used. Authoring the coarse set is optional and it is generated by default —
smallest enclosing primitive per tag group — with a manual override.

### D13 — Normalised surface coordinates are what makes a contact portable

A `SurfaceFrame` stores a point as a **normalised coordinate on a primitive**, not as an offset:

| Primitive | Parameterisation |
|---|---|
| Sphere | two angles |
| Box | face index + `(u, v) ∈ [0,1]²` on that face |
| Capsule / tapered capsule | axial `t ∈ [0,1]` + angle around the axis |
| Cylinder / cone | `t` + angle, with caps as a box's faces are handled |

Scale a shape and the coordinate resolves to a different world point that means **the same place on
the body**. A hand on the belly of a slim character resolves to the belly of a heavy one; the same
clip works on both. This single property is why proxy shapes exist and why a mesh cannot substitute
for them — there is no cheap correspondence between the vertices of two meshes, and there is an exact
one between the surfaces of two boxes.

Where the authored point was not on a surface, a **projection** step supplies one: closest point on
the target primitive, computed at bake, with the residual offset stored separately so a deliberate
1 cm gap survives.

### D14 — Constraints live in clip metadata, over clip time

A `.vxanim`'s sidecar carries a **constraint track**:

```
ConstraintTag
  GoalKind      kind
  Effector      effector          // joint / axis on joint / joint pair
  IConstraintFrame goal
  ChainSpec     chain             // first joint … effector
  float         begin, end        // normalised clip time, [0,1]
  EasingCurve   easeIn, easeOut
  float         maxWeight
  int           priority
  FacetSet      labels
  LodRange      lods
```

**Over clip time, not wall time**, so a clip played at 0.7 speed keeps its contacts where they were
authored; and **normalised**, so a retimed clip does not need re-marking.

**Weight is the product of three things**: the clip's own blend weight in the tree, the tag's
activation from its lifespan and easing, and any suppression a game system applied by label. All
three are continuous, which is what makes the result continuous.

⚠ **This is the row P0 has to build first.** There is no sidecar until `.vxanim` compiles, and the
sidecar's schema must be **open**: a project's own tag kinds round-trip through the importer and the
editor without a fork. See [Part 4](#part-4--the-seams).

### D15 — A goal may be a curve over clip phase

A constant goal covers a contact that does not move. A hand *sliding along* a rail, or *tracking* a
target through a throw, is a **trajectory**: the goal sampled per phase.

Stored as two decimated polylines — the frame's origin, and the offset from it — keyed on phase,
because they compress very differently: the origin usually barely moves while the offset carries all
the shape. Decimation is Ramer–Douglas–Peucker against an authored error tolerance, reusing the
report shape `AnimationCurveCompressor` already produces so an author can see what a tolerance cost.
Runtime interpolation is a phase-keyed lookup and a lerp/slerp.

### D16 — Arbitration is one weighted pass by default, and the policy is an interface

Two goals wanting the same joint chain is the normal case, not the exception, and something has to
decide.

**The shipped default, stated with its limits:**

1. Group active goals by the chain they modify.
2. Within a group, per goal kind, take the **weighted average** of the goals, weights normalised.
3. Where a chain has goals of several kinds, satisfy in the order position → orientation → aim,
   each within the freedom the previous left.
4. **Priority is a weight multiplier and a tie-break**, not a hard ordering: a higher-priority goal
   dominates the average and wins outright when weights are equal.
5. Clamp to joint limits. Report per-goal residual error.

**It is honest about what this is not.** It does not guarantee that a high-priority goal is satisfied
exactly when a low-priority one conflicts; it does not distribute error up the hierarchy towards the
root; it does not resolve cyclic goals (two hands each targeting the other) other than by damping.
Those need a staged solve, and a staged solve is a much larger piece of work with a much larger
authoring surface. The default is the one that is predictable, cheap, and easy for an author to
reason about — which for most content is the right trade.

```
interface IConstraintArbiter {
    void Solve(in ConstraintSolveContext context, Span<BoneTransform> pose);
}
```

A project that needs the staged version installs one. **This is the single most important seam in
the document** and P3's exit criteria include a second arbiter implementation used only in tests, to
prove the interface is not shaped around the default.

### D17 — Temporal continuity belongs to the solver

Per-goal state, keyed by a **stable instance id** (clip + tag index + bound slot), holding: last
applied weight, last resolved goal, last effector position, and an easing state.

- A goal that becomes resolvable **eases in** over its authored duration, from where the effector
  actually is.
- A goal that stops resolving — the entity unbound, the LOD dropped it, the clip blended out —
  **eases out** towards the animated pose, using the last valid goal to interpolate from. It does not
  vanish.
- `Reset()` clears all of it, for a teleport or a camera cut, where continuity is *wrong*.

**This is not polish.** Every visible failure of a constraint system is a discontinuity, and almost
all of them come from a goal appearing or disappearing between frames. It is a decision rather than
an implementation detail because it dictates that the stage is **stateful and per-animator**, which
constrains everything else.

### D18 — Solving is per-character; the grouping is an interface

```
interface IConstraintScheduler {
    void Plan(ReadOnlySpan<ConstraintStack> stacks, IConstraintGroupSink sink);
}
```

The default emits one group per stack, and `AnimationSystem` solves groups in parallel above its
existing `ParallelThreshold`.

**Why the seam exists.** Two characters whose goals reference each other cannot be solved
independently without one of them seeing last frame's pose of the other. Handling that properly means
discovering the dependency, grouping the affected characters, and solving them together before either
one's individual work — which is a real system with real scheduling consequences and is not shipped
here. The interface makes it an addition rather than a rewrite, and `ConstraintSolveContext` carries
a group rather than a single stack from day one so the signature never has to change.

### D19 — The root transform is solved as a body of its own, before the pose

Before any joint moves, the character's **root placement** is adjusted: a small solve over a single
rigid transform with position, orientation and aim goals, no chain and no hierarchy.

It comes first because it is cheap and because it removes most of the error the pose solve would
otherwise have to absorb. A character reaching for a door handle that is 20 cm too far should mostly
*stand somewhere else*, and only then stretch.

Goals labelled `root` participate here and are excluded from the pose solve. The result is offered to
the character controller as a *suggestion* — the controller owns the transform and decides how much
survives a wall, exactly as it already does for `LastRootMotion`.

### D20 — LOD has three knobs, and they are independent

| Knob | What it does | Driven by |
|---|---|---|
| **Rate** | solve every *n*-th frame, holding the previous result | distance, importance |
| **Detail** | which proxy shape set resolves surface frames | distance |
| **Scope** | which chains are disabled entirely — fingers first, then toes, then forearms | distance |

Each goal declares the LOD range it is valid for; dropping out of range eases out through
[D17](#d17--temporal-continuity-belongs-to-the-solver) rather than snapping.

⚠ **Rate is the one with a trap.** Skipping a solve on a character whose *pose* still updates means
the goal is stale, not absent. The stage holds the previous correction and re-applies it as an
offset, which is right for a few frames and wrong for many — so the rate ladder is bounded and the
budget-based governor reports when it hits the floor rather than silently degrading.

---

## Part 2 — the authoring surface

Three surfaces, all following `Vixen.Editor.AnimationGraph`'s pattern: **a serialisable document, and
a compiler that produces the runtime object.**

### The clip editor gains a constraint track

The existing animation clip editor is where a `.vxanim`'s metadata is authored, and constraints are a
track on its timeline beside events.

- A tag is a **bar on the track**: drag its ends to set begin/end, with the ease-in and ease-out
  ramps drawn on the bar itself, so the shape of the activation is visible rather than a pair of
  numbers in an inspector.
- Selecting a bar shows **only the fields its goal kind has**. A position goal has no aim axis; the
  panel does not show one. This is the difference between a markup tool people use and one they
  avoid, and it is generated from the goal-kind schema rather than hand-written per kind.
- **The viewport draws the goal**: the effector, the target frame, the chain highlighted on the
  skeleton, the proxy shape the surface coordinate lives on, and — scrubbing — the residual error as
  a coloured line from effector to goal. An author sees the constraint failing before a build does.
- **Templates.** A named, versioned bundle of tags with relative timings, applied in one action. A
  seated interaction is twenty constraints and nobody authors twenty constraints repeatedly.
  Templates carry a version; re-saving a template offers to re-apply it across every clip that used
  it, and reports what it would change before doing it.
- **Assisted authoring**, not automatic: with a clip and a bound scene, the editor can *propose*
  tags from proximity — this hand is within 2 cm of this shape for this span, offer a position goal
  with that lifespan. The author accepts, edits or rejects. Proposals are never applied silently.

### The proxy shape editor

Part of the model asset editor, not a separate tool. Primitives placed on a skeleton, parented to
joints, sized with handles; tags from a project-defined vocabulary; a symmetry toggle; a generated
coarse set with per-shape override. A validation pass reports shapes that never move, shapes that
overlap suspiciously, and — importantly — **names present in one set and missing from another set
built against the same skeleton**, which is the failure that makes a clip work on one character and
not another.

### The move set editor

A table, because a move set is a table.

- Rows are entries; columns are facets, with the facet vocabulary editable per project.
- **The filter box is a query.** Typing the same query the runtime would build shows what would be
  selected, in score order, with the score breakdown — matched facets, numeric proximity, penalties.
  This is the single most valuable thing in the tool: "why did it pick that clip" is otherwise
  unanswerable.
- Overlays are shown resolved, with overridden rows struck through and their source set named.
- Transition rules are a second list, with a pair-picker that shows which rule matches and why.
- **Coverage.** Given the facet vocabulary and the gait model's numeric range, the editor reports
  which regions of the query space fall back — where the set has no injured stop, or nothing above
  4 m/s. Not an error; the thing an author needs to see before shipping.

---

## Part 3 — the runtime surface

The frame, in order, for one animated character:

```
1  Game code writes MoveIntent and animation parameters
2  AnimationSystem.Update
     └ Animator.Update
         ├ layers evaluate                         (unchanged)
         │   └ MoveSetMotion, where one is in play
         │       ├ query built from parameters + IGaitModel(MoveIntent)
         │       ├ re-selected only if the query changed or the move is ending
         │       └ evaluates the chosen entry's Motion, blending per D5's rules
         ├ root motion extracted                   (unchanged)
         └ pose processors
             ├ ConstraintStack                     ← new
             │   ├ gather active goals from clip tags + game-added requests
             │   ├ resolve frames; unresolved ones ease out
             │   ├ pose the proxy shapes those frames name — and only those
             │   ├ root placement solve            (D19)
             │   ├ IConstraintArbiter.Solve        (D16)
             │   └ write temporal state
             └ existing IK processors              (unchanged)
3  Character controller reads the root suggestion and LastRootMotion, moves the entity
4  SkinningSystem
```

**Adding a goal from game code**, for the cases no clip can know about:

```csharp
var handle = stack.Add(new PositionGoal {
    Effector = skeleton.Joint("hand_r"),
    Goal     = new SocketFrame("held-item", "grip"),
    Chain    = ChainSpec.From("upperarm_r"),
    Priority = Priority.Interaction,
    Label    = Labels.Grip
});

handle.Weight = 0.8f;      // adjustable while it lives
handle.Dispose();          // eases out; does not snap
```

The handle is the whole API: a request goes in, a handle comes back, the handle allows the few things
that are safe to change while a goal is live, and releasing it eases the goal out. Everything a clip
tag does, a handle can do, and they arbitrate together with no special case.

**Querying and suppression**, for a system that must take a body part over:

```csharp
stack.Suppress(Labels.LookAt, weight: 0f);        // a gesture system takes the head
foreach (var goal in stack.Active(Labels.Grip)) { … }
```

**Guarantees.** The stage is allocation-free per solve after warm-up, NativeAOT-clean, and
deterministic given the same pose and the same resolved frames — a requirement, not a nicety, given
[16](16-networking.md). Where a frame resolves through game-provided data, determinism is the game's
responsibility and the API says so.

---

## Part 4 — the seams

**The shipped defaults are deliberately the simple ones.** Every place a project will plausibly want
something more elaborate is an interface, defined and exercised in the phase that introduces it —
not retrofitted. This is the open-closed principle applied on purpose, and it is a stated design goal
rather than a side effect.

| Seam | Interface | What a project can build on it without forking |
|---|---|---|
| Which move to play | `IMoveSelector` | A different selection policy entirely — a feature-vector nearest-neighbour matcher, a learned policy, a table-driven chooser |
| Intent → numeric targets | `IGaitModel` | Quadrupeds, vehicles, flying bodies, swimming |
| Where a goal is | `IConstraintFrame` | Frames resolved from a physics query, a navmesh, a spline, a procedural surface, another game system's own spatial data |
| Who the other party is | `ConstraintBindings` + `IBindingSource` | Named-role multi-party interactions: a table of participants resolved per interaction, with the clip referring to roles rather than entities |
| How conflicts resolve | `IConstraintArbiter` | A staged solve — layered passes, error redistribution towards the root, per-body-section solvers, exact satisfaction of the top layer |
| What is solved together | `IConstraintScheduler` | Dependency discovery between characters and simultaneous multi-character solves, scheduled ahead of individual evaluation |
| Per-chain solving | `IChainSolver` | An analytic solver for a specific limb topology, an iterative one for a long chain, a data-driven one |
| Clip metadata | Open sidecar schema + `IClipMetadataExtension` | Project-specific tag kinds, authored in the same track, round-tripping through the importer and editor untouched |
| Shape posing | `IProxyShapePoser` | Shapes whose size or position is driven by something other than a joint — a scripted deformation, a simulation, a morph weight |
| Selection scoring | `IMoveScorer` | A different scoring function, or extra terms from game state |

**Two rules keep these honest**, and they are enforced in review:

1. **Every interface is implemented at least twice** before its phase is finished — once as the
   shipped default and once in tests, differently enough to prove the shape is not the default's
   shape wearing a mask.
2. **No default is reachable except through its interface.** `ConstraintStack` holds an
   `IConstraintArbiter`, never a `DefaultArbiter`. A seam nobody is forced through rots.

---

## Part 5 — phases

Effort in engineer-months, on [14](14-roadmap.md)'s scale. **P0 is not post-1.0** — it is an owed row
with several consumers. Everything after it is post-1.0, and the two halves are independent after P0:
P1–P2 and P3–P6 can run in parallel or either can be cut without the other.

### P0 — the `.vxanim` runtime path and the metadata sidecar (1.0 EM)

The owed row. An authored `.vxanim` compiles to `AnimationClipData` baked against a skeleton, gets an
artifact key, and loads by address in a build — the move `.vxscene`, `.vxmat` and `.vxcompositor`
have already made. Plus the **open metadata sidecar**: events move into it, the constraint track's
schema is defined, and an unrecognised extension round-trips rather than being dropped.

Exit: `Samples/13`'s `CharacterAnimation` loads its clips by address and stops computing them, and a
sidecar carrying an unknown tag kind survives an import/export round trip byte-identically.

### P1 — move sets and the query selector (1.5 EM)

`MoveEntry`, `FacetSet` and the interning, `MoveSet` and its bake-time overlay, `MoveQuery` and the
scoring pass, `MoveSetMotion`, the `Symbol` parameter type, the biped `IGaitModel`, `IMoveSelector`
and `IMoveScorer`.

Exit: a 500-entry set selects in **under 5 µs**, benchmarked; the same inputs select the same entry
on two platforms; and a set with an injured overlay of three clips produces correct injured
locomotion with no second graph.

### P2 — transition rules and phase sync (1.0 EM)

`TransitionRule` and the first-match evaluator, easing, per-transition masks, `SyncMode.Phase` and
`ClosestFoot`, foot-phase authoring on an entry.

Exit: a walk↔run↔sprint ladder with no visible skate, verified by a foot-slide metric over a
recorded run, not by eye.

### P3 — the constraint stage (2.5 EM)

`ConstraintStack`, the four goal kinds and their region forms, `IConstraintFrame` and the six shipped
frames, bindings, the handle API, the default `IConstraintArbiter`, temporal continuity, the
`IChainSolver` dispatch onto the existing solvers, label suppression and querying.

Exit: allocation-free per solve; a hand goal holds through a blend between two clips that both carry
it, and through one that does not; the second test-only arbiter passes the same suite; and
`FootPlacement`-over-the-stage matches the standalone solver within tolerance.

### P4 — proxy shapes and surface coordinates (1.5 EM)

The `ProxyShape` set as an asset, the seven primitives, normalised surface coordinates and the
projection bake, lazy posing, the coarse-set generator, `SurfaceFrame`, `IProxyShapePoser`.

Exit: **one authored clip, three bodies of visibly different proportions, hand contact correct on
all three** — the claim at the top of this document, demonstrated. Plus: posing cost scales with
active goals, not with shape count, measured.

### P5 — trajectories (0.75 EM)

Phase-keyed goal curves, the two-polyline decomposition, RDP decimation with a tolerance report,
runtime interpolation.

Exit: a sliding-hand contact reproduces on a rail of a different length and radius, with the
compressed curve within the authored tolerance of the raw one.

### P6 — root placement and LOD (1.0 EM)

The single-body root solve of [D19](#d19--the-root-transform-is-solved-as-a-body-of-its-own-before-the-pose),
the controller handoff, the three LOD knobs, the budget governor and its reporting.

Exit: a hundred constrained characters inside a stated frame budget, with the governor's report
naming what it dropped.

### P7 — authoring (2.5 EM)

The constraint track and its generated inspector, the viewport gizmos and the error readout,
templates with versioning and batch re-apply, the assisted-authoring proposals, the proxy shape
editor and its validation, the move set table with the live query and the coverage report.

Exit: a non-programmer marks up a ten-second interaction clip, unaided, and it works.

### P8 — seams and a sample (0.75 EM)

Every interface in [Part 4](#part-4--the-seams) implemented twice, the sample that demonstrates both
halves, and the manual pages.

**Total ≈ 12.5 EM**, of which 1.0 is an already-owed row.

---

## Risks

| | Risk | Mitigation |
|---|---|---|
| **R1** | **The authoring cost is the real cost.** A constraint system with nothing marked up does nothing, and marking up a library of clips is weeks of artist time that no engineering decision reduces | P7 is 2.5 EM and it is the phase most likely to be under-budgeted. Templates and assisted proposals exist for this reason and not for polish. **The honest exit for P4 is one clip, not a library** |
| **R2** | **The default arbiter will not be enough for someone**, and the discovery will come late, from content that already exists | [D16](#d16--arbitration-is-one-weighted-pass-by-default-and-the-policy-is-an-interface) states the limits up front, and P3 ships a second arbiter in tests specifically so the interface is not the default's shape. If the default proves insufficient across the board, a staged arbiter is a further ~2 EM and it is an addition |
| **R3** | **Determinism under replication.** [16](16-networking.md) needs two machines to agree. Selection is deterministic by construction; the solver is deterministic given identical resolved frames, and frames resolved from game data are not the solver's to guarantee | The API documents the boundary. The safe default — and what the sample does — is that **pose is not authoritative**: parameters and the selected `MoveKey` replicate, the pose is reproduced locally |
| **R4** | **Proxy shape sets are content**, and inconsistent naming across characters breaks clip portability silently | Validation in the editor compares sets built against the same skeleton and reports missing names. A project convention is still required, and the tool can only report it |
| **R5** | **Lazy shape posing has a worst case** — a frame where many goals become active at once poses many shapes | Bounded by the active-goal count, which LOD scope already caps. Measured in P4's exit, not assumed |
| **R6** | **Two halves, one document.** The move set is not needed for constraints and constraints are not needed for the move set; a partial delivery could leave neither useful | Deliberate: after P0 the two tracks are independent, each has its own exit, and either can be cut whole. They share this document because they share a subject, not a dependency |
| **R7** | **Scope creep towards motion matching.** The selector is one refactor away from a feature-vector search, and that project is much larger than it looks | [D8](#d8--the-selector-is-a-motion-and-motion-matching-is-not-in-it) rules it out and `IMoveSelector` makes it an addition. It stays out of this plan's budget |
