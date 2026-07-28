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
```

`SkinningRenderFeature` owns the buffer, the upload and the push constant, and its own documentation
says explicitly that whoever fills the palettes is the animation system, because there is no callback
of the renderer's between "animation finished" and "the first palette is written". `SkinningSystem` is
the system it means. What goes in is `inverseBindPose * jointModelSpace`, in **model space** — the
object's own transform is pushed separately and applied after skinning, which is also what lets a
hundred instances of the same animation share a palette.

## Still to come

**A graph asset, and the editor that authors it.** The state machine, layers and blend trees are built
in code today. `Vixen.Editor.AnimationGraph` is the other half of Phase 8, and it needs a serialised
form of these types — which means a discriminated `Motion` hierarchy the `[DataContract]` generator can
round-trip, and that is a design in its own right rather than something to bolt on in passing. Nothing
here is shaped to prevent it: every runtime type is a plain object graph with no runtime-only state
except playback position.

**Retargeting.** A clip is baked against one skeleton and may only be played on that one. Playing a
clip authored on one rig on another needs a bone mapping and a proportion-corrected pose, and neither
exists yet. `AnimationClip.UnresolvedChannels` is what a retargeting mistake currently looks like.

**Key cursors.** Keys are found by binary search. The usual optimisation is a per-track hint remembering
where the last sample landed, which cannot live on the clip — that is shared — and living on the player
means one hint per track per clip per instance, with a blend tree's active set changing as its parameter
moves. Not worth it against a thirty-key track; worth revisiting for clips with hundreds of keys per
track, which is what an uncompressed facial rig looks like.

**Curve compression.** Every key is a full `Vector3` or `Quaternion` at full precision. Doc 08's model
compiler is where quantisation and key reduction belong, and this assembly is shaped to receive them:
sampling reads spans of times and values, and nothing above `AnimationClip.Sample` knows how they are
stored.

**Parallel evaluation.** `AnimationSystem` runs its animators inline. Each one is independent and this
is exactly the shape of work `JobScheduler` exists for, but a parallel version has to answer for the
managed component store's threading first. The loop is written so that becoming parallel is a change
to that one file.

**Ping-pong events and root motion.** A backwards pass would have to fire events in reverse order and
produce root motion that undoes itself. `AnimationClip.Advance` bounces the time correctly and reports
no loops for `WrapMode.PingPong`, which means events fire only within the current segment.

Licensed under Apache-2.0.
