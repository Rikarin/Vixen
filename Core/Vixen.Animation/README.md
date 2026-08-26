# Vixen.Animation

Skeletal animation: a clip becomes a pose, poses blend, layers mix, a state machine decides which,
IK corrects the result, and the whole thing comes out as the bone palette GPU skinning reads.

Spec: [docs/plan/14](../../docs/plan/14-roadmap.md) § Phase 8, and
[docs/plan/06](../../docs/plan/06-rendering-pipeline.md) for the skinning half of it.

```csharp
var skeleton = Skeleton.Create(artifacts.Read<SkeletonData>(skeletonId));
var walk     = AnimationClip.Create(artifacts.Read<AnimationClipData>(walkId), skeleton);
var run      = AnimationClip.Create(artifacts.Read<AnimationClipData>(runId), skeleton);

var animator = new Animator(skeleton) { RootMotion = RootMotionMode.Apply };

var locomotion = new AnimationState(
    "Locomotion",
    new BlendTree1D(animator.Parameters, "Speed", [
        new(new ClipMotion(walk), 1.5f),
        new(new ClipMotion(run),  6.0f)
    ])
);

animator.AddLayer("Base", new AnimationStateMachine([locomotion]));

// Every frame:
animator.Parameters.SetFloat("Speed", controller.Speed);
animator.Update(deltaTime);

foreach (var fired in animator.Events) {
    if (fired.Event.Name is "Footstep" && fired.Weight > 0.5f) {
        audio.Play(fired.Event.String);
    }
}
```

## The shape of it

```
AnimationClipData ──bake──▶ AnimationClip ──▶ ClipMotion ─┐
                                                          ├─▶ Motion ─▶ AnimationState
                                    BlendTree1D / 2D ─────┘                   │
                                                                     StateMachineInstance
                                                                              │
                                     AnimationLayer (weight, mask, blend) ◀────┘
                                                    │
                                              Animator ─▶ SkeletonPose ─▶ IPoseProcessor (IK)
                                                                              │
                                                        ComputeSkinningMatrices ─▶ SkinningRenderFeature
```

Every box is usable on its own. A test drives a `StateMachineInstance` with a `for` loop; a tool
samples an `AnimationClip` with no animator anywhere; `TwoBoneIk` is a static method over a span.
`Animator` is the thing a game holds, and it is a façade over the rest rather than the place any of
it lives.

## Decisions worth knowing about

**Local space is the only space a pose is stored in.** Blending model-space poses stretches the bones
between the joints — the artefact that makes a naïve crossfade look like rubber. Model space is
derived, in one forward pass, at the end. See `SkeletonPose`.

**Time inside a blend tree is normalised, not seconds.** A 1.2-second walk and a 0.8-second run
blended half and half play as a 1.0-second cycle with both feet landing together. Sampling both in
seconds gives a character that appears to have four legs. The tree's length is the weighted average
of its children's, so a character speeding up has its stride rate move continuously rather than jump
when the dominant clip changes.

**Transitions are a stack, not a current-and-next pair.** A transition pushes its destination on top
and fades it in; the states underneath keep playing and keep their own time. Interruption then falls
out rather than being bolted on — an interrupted transition is a third state pushed over two that
were already blending. Capped at four (`StateMachineInstance.MaxConcurrentStates`), because past that
the oldest is contributing under a percent and costing a full motion evaluation.

**Root motion is a mode, not a flag.** A character controller wants `Extract` — give me the delta,
I will decide what survives a wall. A cutscene wants `Apply`. A clip authored in place wants
`Disabled`, where the root joint is just another joint. The delta is expressed in the character's own
frame, so one run cycle works for a character facing any direction.

**Additive layers scale the difference; they do not blend towards it.** An aim offset applied at 40 %
aims 40 % of the way, whatever the locomotion underneath is doing. That is a different operation from
a 40 % blend and it is the reason additive layers exist at all.

**IK is analytic where it can be.** Two bones and a target is a triangle with three known sides, so
the interior angles come out of the law of cosines with no iteration and no convergence threshold.
Only rotations are written, so a chain never stretches: an unreachable target gets a straightened limb
pointing at it, which is information rather than a bug that looks like a bug.

**Ground contacts are supplied, not queried.** This assembly does not reference `Vixen.Physics` and is
not going to. A raycast is a physics-world question asked on the physics thread's terms, and a
character on a moving platform wants to ask it in a way only the game knows. What animation owns is
what to do with the answer — see `FootPlacement`.

**Events are collected, not called back.** An event fires in the middle of evaluating a blend tree,
with the pose half-built and the layer stack mid-flight. A handler that reacted by changing a
parameter or destroying the entity would be doing it to a system that is iterating itself. The buffer
is drained after the pose is finished, which is the only ordering that lets a handler do the obvious
thing.

**Blend weights come out of gradient band interpolation** in two dimensions — Rune Skovbo Johansen's
construction, which Unity's freeform modes also use. Inverse-distance weighting would give every
motion a non-zero weight everywhere, so a backward walk would always be slightly playing during a
forward run; a Delaunay triangulation gives exact weights and needs a triangulation, which is a build
step and a degenerate-case problem and something to rebuild whenever an artist drags a motion.

**Retargeting transfers the animation, not the pose.** For every mapped joint, the source's
model-space rotation is measured against its own bind pose — that difference is what the animation
does, with the rig's resting shape divided out — and the same difference is applied to the target's
bind pose. A clip authored on an A-pose rig therefore plays on a T-pose one. The cheap alternative
works in local space, needs no chain, and would let a clip be retargeted channel by channel with its
keys intact; it is also wrong whenever the two bind poses differ by a rotation anywhere up the chain,
which is the only case worth retargeting for. See `Retargeting/SkeletonRetarget`.

**Keys are found by an index, not a cursor.** Each long track carries a table of one entry per key
mapping a uniform slice of the clip's duration to the key at or before it, so a lookup is a multiply,
an array read and about one comparison whatever the track's length. A cursor — the usual answer —
cannot live on the clip, which is shared by every instance playing it, and on the player it is a hint
per track per clip per instance over a set of clips that moves as a blend parameter does. The index is
the same win with no state, which also makes it correct for a seek and safe to sample from several
threads. Four bytes a key, and tracks of eight keys or fewer skip it and binary-search. What it buys
is measured below, and the headline is that seeking costs what playing forwards costs.

## ECS and the renderer

`AnimationSystem` runs in `SystemPhase.Animation` — which the ECS defines as "after logic has decided
what is playing" — and writes root motion into `LocalTransform`, so `TransformSystem` composes the
world matrix from it in `PreRender`. `SkinningSystem` then fills the palettes.

```csharp
var entity = world.Create(
    new AnimatorComponent { Value = animator },
    LocalTransform.Identity,
    new SkinnedRenderer { RenderObject = renderObjectId }
);

runner.Add(new AnimationSystem());
runner.Add(new SkinningSystem { Renderer = renderSystem, Feature = skinningFeature });
runner.Add(new BlendShapeAnimationSystem());
```

`AnimationSystem` gathers its animators on this thread, evaluates them across the `JobScheduler`, and
publishes root motion back on this thread. The split is what makes it safe: an animator is reached
through a managed component and touches nothing outside itself, so the middle phase can run anywhere
as long as it cannot see an `Entity` — and it cannot. Below four animators, or with no scheduler,
it runs inline; scheduling three characters costs more than animating them.

`SkinningRenderFeature` owns the buffer, the upload and the push constant, and its own documentation
says explicitly that whoever fills the palettes is the animation system, because there is no callback
of the renderer's between "animation finished" and "the first palette is written". `SkinningSystem` is
the system it means. What goes in is `inverseBindPose * jointModelSpace`, in **model space** — the
object's own transform is pushed separately and applied after skinning, which is also what lets a
hundred instances of the same animation share a palette.

### Blend shapes

`AnimationChannel` carries a fourth track that is a **scalar**, and it drives a blend shape:
`Shape`, `WeightTimes`, `Weights`. A model import fills it from a glTF `weights` sampler or an FBX
morph channel; `AnimationClip` bakes one flat track per shape, with the bucket index the vector tracks
get; `ClipMotion` collects it into `Animator.MorphWeights` as the blend tree is evaluated, scaled by
what that clip is contributing; and `BlendShapeAnimationSystem` lands it on the renderer's
`BlendShapeWeights` component. `AnimationSystems.AddAnimation` registers it, so a game wires nothing.

`MorphWeightBuffer` is `AnimationEventBuffer` and `ConstraintTagBuffer`'s shape and exists for their
reason: a weight comes out of a clip halfway through a tree, at a point where the pose is half-built,
so it is collected and read afterwards.

- ⚠ **A shape is bound by *name*, and the slot it lands in comes from `BlendShapeWeights.Shapes`**,
  which the renderer publishes out of what it actually attached. The ordinal a source file uses is not
  the mesh's — the import drops a shape that moves nothing above its threshold — so a curve stored
  against an index would silently re-target itself on the next export.
- ⚠ **A weight of zero is a value and an absent track is not.** `WeightTimes.Length` is what says a
  clip drives a shape at all; the buffer keeps membership and value separate for the same reason, and
  the system writes only the slots a clip named. Writing all of them would make playing a wave
  animation wipe an expression a script had set.
- ⚠ **Weights add across clips and across layers.** Inside a tree that is exact, because a tree's child
  weights sum to one. Across layers it is additive rather than an override — which is what a facial
  layer wants, and is a stated limitation rather than a rounding of one: the machinery that models an
  override works on joints, and a shape is not one.
- ⚠ **Collected at the clip's own time, in seconds** — unlike a constraint tag, whose span is authored
  as a fraction of the cycle. A one-second clip cannot tell the two apart, which is why the test that
  guards it uses a four-second one.
- ⚠ **`AnimationClip.UnresolvedChannels` does not count a weight channel**, which names the morphed
  mesh's node rather than a joint. Counting them would report a head's worth on every correct import.
- **A `.vxanim` written by hand drives a shape too**, as of 2026-08-26. `AnimationProperty.Weight`
  with `AnimationCurveData.Shape` beside it is the authored form, and `AnimationClipAsset.ToClipData`
  bakes each weight curve into the same named scalar channel an import would have written.
  `Samples/03-PbrShowcase/Assets/Animation/expression.vxanim` is the first one.
- ⚠ **A rig-free weight sampler, for the same reason `TrySample` exists.**
  `AnimationClipContent.TrySampleWeight(shape, seconds, out weight)` is what a morphed mesh with no
  skeleton uses — a head that is one mesh has no joint to resolve a channel against, and
  `AnimationClip.Create` needs one. A character with an `Animator` takes the baked path above; the
  bool is the fact either way, because a false read as zero turns an additive facial layer into an
  accidental override.

## Move sets and pose constraints

Two namespaces that sit on the machinery above rather than beside it, both from
[doc 34](../../docs/plan/34-move-sets-and-pose-constraints.md).

**`Vixen.Animation.Moves`** — a character's movement vocabulary as a flat catalogue of clips tagged
with interned facets, and picking one as a scored query rather than as a graph edge. `MoveSetMotion`
is a `Motion`, so a state holds one exactly as it holds a clip and every layer, mask, event and
root-motion path above it is unchanged. `ITransitionPolicy` decides what happens between two moves;
`SyncMode.ClosestFoot` carries a cycle across a change by aligning **contacts** rather than
fractions, because where a clip's cycle starts is a fact about how somebody trimmed it and where it
plants is a fact about the character.

**`Vixen.Animation.Constraints`** — authored spatial goals, arbitrated and applied after the layer
mix. `ConstraintStack` is an `IPoseProcessor`, which is the whole of how it attaches:

```
clip's ConstraintTrack ─┐
                        ├─▶ ConstraintStack ─▶ IConstraintFrame.TryResolve  (where is it?)
   stack.Add(goal) ─────┘          │           ease in / ease out           (D18)
                                   ▼
                          IConstraintArbiter    absolute averaged, additive summed on top
                                   │
                                   ▼
                            IChainSolver ─▶ TwoBoneIk
```

Four goal kinds — position, orientation, aim, distance — each with a region form and an additive
form. **Additive is a mode and not a variant**: a recoil is an offset from the aim pose and an
absolute goal would fight the aim, and two recoils have to *sum*, because averaging them would make
the second shot weaker than the first.

**An aim goal stores an angle and a distance, not a point.** Store the point and the same authored
intent sprays past the window at twice the range; storing the deviation and rescaling it by the ratio
of authored to current distance keeps the point of aim on the object.

**A residual belongs to the instance, not the goal.** A goal reached through a clip's track is one
object shared by every character playing that clip, so a per-goal error field would have a hundred
writers and one value. `ConstraintHandle.Residual` and `ConstraintStack.Residual(track, index)` are
where it lives, and it is public API rather than a debug readout — a goal that cannot report why it
failed is a goal an author cannot fix.

**A contact is a normalised coordinate on a proxy shape, which is why they exist.** Scale a shape and
the same `(face, u, v)` resolves to a different world point that means the *same place on the body* —
a hand on the belly of a slim character resolves to the belly of a heavy one. There is no cheap
correspondence between the vertices of two meshes and there is an exact one between the surfaces of
two boxes, which is also why a mesh cannot substitute. Three other forms exist because a surface patch
is not always what was meant: an **axis** out of the shape's centre tracks proportions instead, a
**limb** fraction needs no shape at all, and origin, orientation and scale may each name their own
source — which is the general case the other three are special cases of.

**Shapes are posed lazily, and a socket is adapted rather than read.** A character may carry a hundred
proxy shapes and a frame typically touches two to six, so the stage collects the shapes the active
goals name and poses only those. An attachment socket goes further: its offset from the bone was
authored against one hand, and preserving it drives a pistol into a bigger palm — so the socket names
a coordinate on the hand's own proxy shape and the solve moves the socket, not the arm.

**A goal that moves is a goal sampled per phase.** A hand sliding along a rail is a `TrajectoryFrame`
wrapped around whatever frame says where the rail is — a wrapper rather than a sixth kind of frame,
because "this goal moves" is orthogonal to "this goal is on a socket". It is stored as two decimated
polylines, the frame's origin and the offset from it, because they compress very differently: the
origin usually barely moves while the offset carries all the shape. What actually replays for a
surface contact is a path in *normalised* coordinates, so the same slide runs the length of a rail of
any size.

**A character's placement and a camera are the same problem, and share the same solver.** Both are a
single rigid transform with position, orientation and aim goals and nothing below them, so
`RigidBodySolver` does both — which means the camera inherits regions, additive goals, weights and
priority for free. Goals labelled `root` are solved as *where the character should stand*, before the
pose and excluded from it, because a character reaching for a door handle twenty centimetres too far
should mostly stand somewhere else and only then stretch; the result is a suggestion the controller
may refuse, exactly as `LastRootMotion` is. Goals labelled `camera` are solved after the shot and
nowhere else, and `ScreenFrame` is the frame that makes a framing constraint an ordinary position
goal: it answers *where the camera would have to be* for a subject to land at a given place in the
picture.

**A satisfied region goal takes no share.** This is not an optimisation. A world volume bounding where
a camera may go is a high-priority region goal that is satisfied almost all the time, and one that
still dominated the average would pin the camera wherever it happened to be and starve the framing
goal underneath it. "Satisfied anywhere inside" has to mean *silent* anywhere inside, or a bound is a
pin.

**A constraint is authored as exactly what it ships as.** A clip's curves are authored as tangents and
shipped as samples, so those need two types and a compile step; a constraint is names, numbers and a
discriminator either way, and the only work the pipeline does is checking it. `GoalKindSchema` is what
the inspector is generated from — a position goal has no aim axis and the panel does not show one —
and a test walks the record demanding every field be either on a panel or deliberately hidden, which
is the only thing that stops the two drifting.

**The frame has a stage before any animator evaluates.** `IConstraintScheduler.PlanPreEvaluation`
runs over every stack in the world first, and the default plans nothing — the cost, measured, is
indistinguishable from a build without it. It exists because grouping characters whose goals
reference each other cannot be done in the pose stage: that runs *after* every member has already
mixed its layers against a stale view of the others.

### Traps

Each of these was a working program with a wrong answer. They are collected here because the
overview's rows point at this file rather than restating them.

- ⚠ **The default pole is the one that makes every solve silently do nothing.** `TwoBoneIk` takes its
  bend plane from the pole and refuses the solve when the pole and the chain's own plane are both
  degenerate — which a straight chain, i.e. most bind poses, makes them. A pole "sensibly"
  extrapolated along the chain is exactly the useless one. A bent chain keeps its plane; a straight
  one bends towards the target.
- ⚠ **A sphere scaled per axis is an ellipsoid and both halves of the pair have to know it.** The
  parameterisation is on the unit sphere with the extents applied afterwards, and the normal is *not*
  the direction to the point. Assuming it is puts a hand 2 % off the belly of every non-uniform body
  — precisely the failure proxy shapes exist to prevent.
- ⚠ **A surface frame's basis is `along × up`, never `up × along`.** The other order decomposes to a
  rotation with a negative scale, silently, and surfaces much later as a mirrored contact.
- ⚠ **A trajectory's `U` is an angle, so it is unwrapped before decimation and re-wrapped after.** A
  slide across the seam reads 0.98 → 0.02, and a decimator handed that either keeps every key there
  or averages through it and sends the hand the long way round the limb.
- ⚠ **The authored origin polyline is not a runtime fallback.** The frame resolves live; a rail that
  has moved since the capture is the ordinary case.
- ⚠ **Trajectory phase is carried per goal, not per character**, and it comes off the live tag rather
  than the goal, whose object is shared by every character playing the clip. A walk at 0.3 under a
  reach at 0.8 is normal.
- ⚠ **The LOD governor reserves the floor for everyone still waiting.** Spending the budget on the
  most important characters in order gives the first thirty-seven everything and strands the rest,
  reporting a hundred characters as over budget when all hundred would have fitted one-in-four.
- ⚠ **The overlap audit exempts only *adjacent* joints.** Exempting every ancestor exempts almost
  everything, because a hand is a descendant of the spine — and a hand buried in the belly is the one
  thing worth reporting.
- ⚠ **The joint-limit clamp cuts and does not redistribute.** A solver that knew about limits could
  bend more at the elbow because the shoulder ran out; this removes the parts of the correction a
  joint may not do and reports the larger residual. Same limitation the arbiter already states.
- ⚠ **`Limited` is a flag, and a zero limit is not "free".** Zero swing and zero twist is a welded
  joint, which is legitimate to author and is also what every rig exported before the fields existed
  deserialises to.
- ⚠ **A mirror swaps the side of the *joint* as well as the position.** Keeping the joint puts the
  left palm on the right wrist, which is right in the bind pose and wrong the moment either arm moves.

**What the editor has to resolve, and why it resolves it that way.** `EditorAnimation` is the one
place that turns an asset path into a rig, a shape set, a vocabulary, a ladder, a clip or a move set;
`EditorApplication.Bound` sets its hooks as each document opens, because a resolver subscribed per
*view* would leave one document carrying four of them.

- ⚠ **A `.vxproxyshapes` names its rig, and the bake deliberately ignores it.** An authoring-time
  reference only: a set is worn by whichever body loads it, which is the whole reason a shape names
  its joint instead of indexing it. Likewise a clip's authoring-context reference — `ToContent` drops
  it and the runtime record has no field for it at all.
- ⚠ **The rig is read from the model's *source*, not from the built asset**, and with the import
  settings off the model's own sidecar. The built catalog does not exist until the project has been
  imported once, and a shape editor that opened only after a successful content build would be
  unopenable in the exact situation somebody opens it. Reading with default settings would place the
  shapes on a body a metre tall — scale and axis conversion are settings.
- ⚠ **Effectors are derived from the body's own proxy shapes**, because nothing declares them; the
  chain root is two joints up, because wrist–elbow–shoulder is what `ChainSolver` is written for. A
  shape on the root is excluded — in an augmented set the root carries everything the scene put there.
- ⚠ **Rigs are cached against the file's write time**, because the panel asks on every keystroke.

## Retargeting

A clip is baked against one skeleton and may only be sampled onto that one. `SkeletonRetarget` is how
it reaches another:

```csharp
var map = RetargetMap.Between(mixamoRig, ourRig)
    .ByName("mixamorig:")                 // most joints agree once the prefix is gone
    .Map("mixamorig:Spine2", "Chest")     // the three that do not
    .SetMode("Hips", RetargetMode.RotationAndTranslation)
    .Build();

var walk = new SkeletonRetarget(map).Bake(mixamoWalk);   // an ordinary clip on ourRig
```

Check `map.MappedJointCount` after a `ByName`: three of sixty is two rigs that do not share a naming
convention, and it is the number that says so before anybody watches the result.

**Baking, rather than retargeting every frame.** The output is an ordinary `AnimationClip` on the
target skeleton, so blend trees, masks, layers and the state machine all work on it without knowing,
and the per-frame cost is zero. `SkeletonRetarget.Apply` is the same transfer for a pose, which is
what a live capture or a procedural source needs.

Resampling is the one lossy step and it is unavoidable: the transfer composes the whole chain, so a
target joint's curve depends on its ancestors' and no per-channel operation could carry the source's
keys across. Bake at a higher rate for anything with a snap in it, then run the compressor over the
result to take back what the uniform grid wasted.

## Compression

Two independent halves, in two places, because only one of them can live in the asset.

**Key reduction** — `AnimationCurveCompressor`, a build-time pass over `AnimationClipData` (doc 08's
model compiler is where it belongs). An exporter emits a key per frame per channel whether or not
anything changed; this keeps a key only where dropping it would move the sampled curve further than
the caller accepts. It fits against the anchor rather than against the neighbours, which is what stops
error accumulating over a long span — the version that checks only the key being dropped lets a
hundred individually-fine keys drift a long way from where they started.

**Packed rotations** — `PackedQuaternion`, the runtime clip's storage. Smallest-three in sixty-two
bits: eight bytes instead of sixteen, for an angular error under 3 × 10⁻⁶ radians. This one cannot be
in the asset, because `AnimationClipData` is the contract a content build writes and the object
database stores — changing its storage is a re-import of every animation ever built. A runtime clip is
baked at load, so its storage is nobody's contract.

## Still to come

**A graph asset, and the editor that authors it.** The state machine, layers and blend trees are built
in code today. `Vixen.Editor.AnimationGraph` is the other half of Phase 8, and it needs a serialised
form of these types — which means a discriminated `Motion` hierarchy the `[DataContract]` generator can
round-trip, and that is a design in its own right rather than something to bolt on in passing. Nothing
here is shaped to prevent it: every runtime type is a plain object graph with no runtime-only state
except playback position.

**Position and scale quantisation.** Rotations are packed; positions and scales are still full
`Vector3`. Doing them well needs a per-track range so the sixteen bits are spent where the track
actually goes, which is analysis the model compiler is better placed to do than the loader — and
rotation tracks are the ones that dominate a skeletal clip anyway.

**Retargeting proportions beyond a scalar.** `TranslationScale` is one number derived from the two
rigs' pelvis heights. A character with long legs and a short torso wants a per-chain ratio, and one
whose arms reach for a fixed prop wants IK on top of the retarget rather than a scale at all.

**Ping-pong events and root motion.** A backwards pass would have to fire events in reverse order and
produce root motion that undoes itself. `AnimationClip.Advance` bounces the time correctly and reports
no loops for `WrapMode.PingPong`, which means events fire only within the current segment.

## Numbers

`Benchmarks/Vixen.Benchmarks.Animation`, run on a ten-core M1 Max with BenchmarkDotNet's short job.
Sixty-four joints, a key a frame at thirty hertz. Short-job variance is wide, so read the shape and
not the third digit.

```bash
dotnet run -c Release --project Benchmarks/Vixen.Benchmarks.Animation
```

**Sampling — one `Sample` over the whole skeleton, playing forwards against seeking at random:**

| Keys per track | Sequential | Random seek |
|---|---|---|
| 30 | 10.7 µs | 9.1 µs |
| 300 | 11.3 µs | 14.2 µs |
| 900 | 12.4 µs | 9.4 µs |

The point is not the absolute figure, it is that **the two columns are the same** and that thirty
times the keys costs about fifteen percent more. A seek is what a transition offset, a blend-tree
threshold crossing and an editor scrub all do, and it is precisely where a cursor is at its worst —
it has nothing useful remembered and pays a search on top of the work. The residual growth with key
count is the working set getting larger, not the lookup getting slower.

**Crowd — `AnimationSystem` over N characters, inline against the job scheduler:**

| Characters | Inline | Scheduled |
|---|---|---|
| 16 | 193 µs | 519 µs |
| 128 | 2.30 ms | 0.31 ms |
| 1024 | 12.7 ms | 8.3 ms |

Sixteen characters is **slower** scheduled — waking workers for a few hundred microseconds of work
costs more than the work — which is where `ParallelThreshold` comes from. It is set to 32, above the
loss and below the win, and it is a number to re-measure on a machine that is not this one.

**Key reduction — a one-second walk on a sixty-four-joint rig, 3 968 keys in:**

| Tolerances | Keys kept | |
|---|---|---|
| `Default` (0.1 mm, 0.06°) | 750 | 18.9 % |
| `Aggressive` (1 mm, 0.5°) | 369 | 9.3 % |

Five times smaller at tolerances chosen to be invisible, ten at tolerances worth a screenshot before
shipping — before the packed rotations, which halve what is left of the rotation tracks.

**The constraint stage — a hundred characters, with and without it:**

| | Per frame | |
|---|---|---|
| No constraint stacks in the world | 501 µs | baseline |
| Stacks present, default scheduler, no goals | 484 µs | ratio 0.97 |
| Two solved goals per character | 891 µs | ratio 1.78 |
| Two goals, one a surface contact, 8 proxy shapes | 903 µs | ratio 1.80 |
| The same, on a body carrying **120** proxy shapes | 909 µs | ratio 1.82 |

Two things are being claimed here. The first two lines: the frame gained a pass before evaluation that
ships doing nothing, and the only defensible answer to what that costs is *nothing you can measure*.
The last two: fifteen times the proxy shapes for 0.7 % more time, which is inside the error bars —
posing follows the goals and not the set. Zero allocation on all five.

## Knowing when a clip is finished

`VariationHarness` plays one interaction across a range of bodies, props and ground and measures it
four ways: how far each goal missed, how far a contact sank into what it was resting on, how hard an
effector changed velocity, and whether a chain ran out of reach. The report is a matrix — variation
against goal — and every cell carries the moment its worst reading happened, which is what lets an
editor drop somebody on the frame rather than telling them a clip is wrong somewhere.

Nothing in it touches a graphics device, a window or the ECS, so a build machine runs it exactly as
an editor does.

⚠ **A goal is judged on how far it missed when it was being asked for in full.** Every tag eases in
and a goal at half weight is supposed to be half satisfied; the first version measured both as error,
and would have flagged every clip ever marked up.

⚠ **A hand that snaps is caught by the velocity measurement and by nothing else.** The residual is
small on both sides of a snap, which is exactly what makes one invisible in a residual plot.

## Seeing it

`ConstraintGizmos` draws what the last solve did — the effector, the resolved frame, the chain the
solver was allowed to move, the proxy shape a surface goal is anchored to, and a line from where the
effector ended up to where it was wanted, graded green to red. It reads `ConstraintStack.LastSolved`
rather than re-resolving, so what is drawn is what happened, and it goes into `DebugDraw` rather than
into an editor viewport — which is what makes it testable with no window and usable in a shipping
debug build.

Licensed under Apache-2.0.
