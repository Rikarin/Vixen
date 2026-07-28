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

## Owed

- **`NetworkBones`** — replicating the pose itself, for the cases the determinism assumption does not
  cover: ragdolls, IK against local geometry, procedural motion. Expensive by nature, and it wants
  the same quantisation treatment the rotation codec already has.
- **Layers past the first.** Only the base layer's state is sent; additive and masked layers are
  driven by the parameters that are already on the wire. A game whose upper-body layer has its own
  machine driven by something *not* replicated would need those too.
- **More than sixteen parameters.** The block is fixed-width because that is what lets an unchanged
  parameter cost one bit; a machine with more should replicate the rest itself.
- **Animation events on the receiver.** They fire locally, which is right, but nothing yet says
  whether a *networked* event — a footstep everybody hears — should be one of those or an RPC.
