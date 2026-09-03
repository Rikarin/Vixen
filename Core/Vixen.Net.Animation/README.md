# Vixen.Net.Animation

Networked animation: the animator's **inputs** go on the wire, not its output.

## The decision, and what it costs

An animator turns a handful of parameters and a state machine position into a pose of every bone,
every frame. Sending the pose is sending the *result* of a calculation the receiver is perfectly
capable of doing — sixty bone rotations a frame per character, against a dozen values that change
when the player presses something. The saving is not marginal; it is the difference between a
networked crowd and a networked pair.

What it costs is a **determinism assumption**, and it is worth stating rather than discovering. The
receiver reproduces the pose only if its animator reaches the same state from the same parameters —
the same clips, the same transitions, the same conditions. That holds for an ordinary state machine
driven by gameplay. It does not hold for anything driven by local physics, IK solved against local
geometry, or a random number generator. Those want a pose replicator, which is expensive and honest
about it — see Owed.

`NetworkAnimatorApplySystem.CorrectedCount` is where that assumption fails visibly: a few corrections
are late joiners and lost packets, a lot means the receiving animator is not reaching the same state
from the same inputs.

## Two components, because they change at different rates

| | | |
|---|---|---|
| `NetworkAnimatorParameters` | reliable | changes when somebody presses something |
| `NetworkAnimator` | unreliable | state, normalised time and speed — changes every tick |

The channels are the opposite way round from what a first guess suggests, and each is deliberate.

A **lost parameter edge does not heal.** A state machine's position is a function of every parameter
it has ever seen, so a missed "jump was pressed" is a jump that never happens on one client and a
machine that is wrong from then on. That is worth paying reliability for.

A **lost state is a tick late**, because the state is re-sent every tick — so it goes unreliably, and
it doubles as the backstop that makes a missed parameter edge recoverable rather than permanent. It
is also what a late joiner needs, having no history to have derived the state from.

Splitting them is what the delta encoder rewards: a component is the unit of change, and folding a
once-a-second parameter in with a once-a-tick time would make the parameter pay attention every
frame.

## The state is a correction, not a command

Calling `Play` every tick would restart the state every tick and nothing would ever animate. So the
state is applied **only when the receiver's machine is somewhere else**, and with a short crossfade
rather than a cut — the thing being corrected was already wrong, and making it wrong *and* jarring is
the worse of the two. There is a test for exactly this, because the obvious implementation presents
as the animation being broken rather than as the network being wrong.

## Authority

`NetworkRules.Write`, the same question a rigid body asks. One policy per object rather than one per
subsystem — `rules.Set(character, NetworkRules.OwnerAuthoritative)` moves the animator and the body
together, which is what anybody setting it would expect.

## `NetworkBones` — the fallback, and it is meant to look expensive

For the cases the determinism assumption does not cover: a ragdoll driven by the local solver, IK
against local geometry, procedural motion with a random number generator in it. Every one of those
produces a different pose on every machine from identical inputs, and no amount of care with
parameters fixes it.

Three things make it affordable enough to exist.

**Rotations only, because a skeleton is rigid.** Bone lengths do not change, so a joint's translation
is its bind pose and sending it would be sending a constant sixty times a second. Where the
*character* is stays `NetworkTransform`'s answer, as it is for everything else.

**A selected subset, not the skeleton.** A humanoid rig is sixty joints and a ragdoll is driven by
about sixteen — the fingers follow the hand and nobody watching a corpse fall can tell.
`NetworkBoneSelection` says which, and it is **not replicated**: it comes from the same content on
both peers, the same argument the prefab id makes. `MaxBones` is 24, deliberately short of a whole
humanoid rig, because a design that let you send sixty would make the expensive choice the easy one.

**Stored packed, not as quaternions.** A bone that did not move is then *bit-identical* to last tick,
so the delta codec spends one bit on it rather than comparing two floats that differ in their last
place — and the component is a quarter of the size in a chunk. `MathCodec.PackRotation` is the
32-bit smallest-three encoding as a value; `WriteRotation` is now written in terms of it, and the wire
golden is what says the refactor changed no bytes.

The cost, stated rather than discovered: **776 bits whole, about 15 kbit/s per character at twenty
updates a second.** The delta takes most of that back for a pose that is partly still, and a ragdoll
in free fall pays close to the full price. That is the trade, and it is why the animator replicates
its inputs.

Both systems run in `SystemPhase.LateUpdate` — after `AnimationSystem` has produced the pose and
before `SkinningSystem` consumes it. That is an ordering *guarantee* rather than a hope about the
dependency graph, because what they touch is a managed `Animator`'s pose and no declared component
access describes it.

There is **no crossfade**, unlike the animator's state correction. A state correction is rare and a
visible cut is a bug; a pose arrives every tick, and blending each into the last would be a low-pass
filter on the animation — a ragdoll that lands softly on every impact. Smoothing a pose belongs in
`SnapshotBuffer`, at the layer that already knows about interpolation delay.

## Owed

- ~~**Per-bone quantisation by importance.**~~ Built: `NetworkBonePrecision` is a per-slot table the
  replicator takes, and `Uniform(6)` is 488 bits against 776 with a bone that did not move still
  costing one. ⚠ **The suggestion this line used to make — that the selection is the natural place to
  say so — is wrong, and worth keeping as the reason.** `NetworkBoneSelection` is per-entity, and a
  per-entity precision is a wire format that varies per entity: the delta codec checks one fixed lane
  width and the connection baselines are compared against one layout, so nothing on either side could
  parse it. It is exactly the argument `NetworkTransformAxes` already makes for the mask belonging to
  the replicator rather than to the entity. What the selection does own is the *ordering* the table is
  read against, which is why the table is indexed by slot and a game using one has to order every
  character's selection the same way. See `docs/guide/engine/pose-precision.md`.
- **Interpolating a pose.** `SnapshotBuffer` interpolates a transform; a pose wants the same treatment
  and does not have it, so a received pose is applied at whatever rate it arrives.
- **Layers past the first.** Only the base layer's state is sent; additive and masked layers are
  driven by the parameters that are already on the wire. A game whose upper-body layer has its own
  machine driven by something *not* replicated would need those too.
- **More than sixteen parameters.** The block is fixed-width because that is what lets an unchanged
  parameter cost one bit; a machine with more should replicate the rest itself.
- **Animation events on the receiver.** They fire locally, which is right, but nothing yet says
  whether a *networked* event — a footstep everybody hears — should be one of those or an RPC.
