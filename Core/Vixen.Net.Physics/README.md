# Vixen.Net.Physics

Lag compensation: the server-side rewind that decides whether a shot fired 80 ms ago hit what the
player was actually looking at.

## The problem, and why it is not optional

A player with an 80 ms round trip aims at a running target, fires, and the packet reaches the server
40 ms later — by which time the target has moved a third of a metre and the shot misses something the
shooter watched themselves hit. Snapshot interpolation makes it worse rather than better: the client
was not even rendering the newest state it held, it was rendering *behind* it, by a jitter-sized margin
([`TickManager.InterpolationDelayTicks`](../Vixen.Net/Time/TickManager.cs)).

Without compensation, hitting anything moving requires leading it by an amount that depends on your own
latency. Players do not experience that as latency. They experience it as the game being broken.

## The answer, and who pays for it

The server keeps a short history of where every compensated body was, moves those bodies back to where
the shooter saw them, asks physics the question, and puts them back.

**The cost is paid by the person who was shot.** They had already moved; from their side they were
killed after reaching cover. That trade is not avoidable — it is the one every server-authoritative
shooter makes — which is why [`LagCompensationSettings`](LagCompensationSettings.cs) is where it gets
decided rather than assumed. Every number in it is a fairness decision, not a performance one.

```csharp
var compensator = new LagCompensator(physics, session.Options.TickRate);
compensator.Track(playerBody);            // players and vehicles; not the walls

// once a tick, beside the replication capture
compensator.Capture(session.Tick);

// handling a hit claim from a client
using var rewind = compensator.RewindFor(claim.Tick, player.RoundTrip.RoundTrip);
if (physics.Raycast(claim.From, claim.Direction, weapon.Range, out var hit)) { … }
```

## Three decisions worth knowing about

**Only tracked bodies move.** Static geometry did not go anywhere, so rewinding it would be work with
no effect, and the tracked set is tens of bodies rather than thousands. It also means the walls stay
where they are during a rewound query — which is what makes a shot through a doorway resolve against
the doorway as it is *now*, and doorways do not move.

**Nothing believes the client.** A hit claim names a tick and the client chooses that number.
`ClampFor` is the rule that decides what it is allowed to mean, and it is public and separately tested
because it is the anti-cheat surface. Three bounds, each of them somebody trying something: not in the
future, not past `MaxRewind`, and **not further back than the player's own measured round trip makes
plausible**. Someone on a 20 ms connection claiming to have been looking at the world 200 ms ago is
claiming to have been shown something they were not shown.

Claims are **clamped rather than refused**. A refusal punishes a player for their latency by discarding
the shot; a clamp resolves it against the oldest world they could honestly have seen. `ClampedCount` is
how often that happened — a few is a bad connection, a lot from one connection is a question for
whoever reads the counter.

**The restore is a `using`, and that is load-bearing.** A world left in the past does not fail. It
simulates, replicates and looks entirely normal, with every player standing where they were a fifth of
a second ago, for ever. A rewound query is a handful of lines, every one of which can throw or grow a
branch six months later that forgets to put the world back — so `RewindScope` makes it the compiler's
job instead of yours. There is a test for a query that throws mid-rewind, because that is the path
nobody writes by hand.

## The history ring

Per tracked body, a fixed ring of poses written round, allocating once. Same shape as
`CaptureRing` in the replication layer and for the same reason, but searched differently: that one is
looked up by an *exact* tick because a delta names the capture it was measured from; this one is looked
up by a tick that **falls between** two entries, because nobody saw the world on a tick boundary.

It is walked rather than bisected. A `Tick` is modular and deliberately has no ordering — see `Tick`'s
own remarks — and bisecting a modular sequence is the bug that reproduces once every two years of
uptime. The ring holds a couple of dozen entries.

Interpolation between the bracketing captures is on by default and is worth the arithmetic: at 30 Hz a
body moving 6 m/s covers 20 cm between captures, so snapping to the nearer one puts it up to 10 cm out
— most of the width of a head. Rotations use the engine's `Nlerp`, which already takes the shorter arc;
`Slerp` would be more correct and the two answers are identical after the ten bits the rotation was
quantised to on the wire.

`Capture` allocates nothing. It is the half of this that a hundred players multiply — a rewind happens
once per shot, a capture happens once per tick per body — and there is a test that says so, bracketed
by a collection count so that it is measuring allocation rather than the artefact described in
`FuzzSession.Weigh`.

## Why this is a package of its own

`Vixen.Net` and `Vixen.Physics` may not reference each other, so the type that has to see a `Tick` and
a `BodyHandle` lives above both — the same argument [`Vixen.Net.Engine`](../Vixen.Net.Engine) makes.
Concretely: a game with networking and no physics must not link Jolt to send a packet, and a game with
physics and no networking must not carry a tick history it never captures.

## Authority is a rule, not a flag

Which peer decides where a body is comes from `NetworkRules.Write` — the same registry that already
answers who may spawn, despawn, call and hand over an object. PurrNet spells this as a per-component
`Owner Auth` toggle; doc 16 calls the rules registry PurrNet's best idea precisely because it makes
"who may do this to that object" one question with one answer, and a boolean beside it would be a
second policy that can disagree with the first.

```csharp
rules.Set(crate, NetworkRules.OwnerAuthoritative);   // the holder simulates it
```

The capture and correction systems ask the same question and take opposite branches, so they can
never both act on one body — which would be the authority correcting itself toward its own last
packet, and is the shape of a body that slowly drifts to a halt. With no registry the answer is the
default `NetworkRules` already states, server-authoritative; note that this is a statement about the
*object*, so whether **this peer** is that authority still depends on whether it is the server.

## Correcting a body somebody else simulates

A remote body is simulated locally from the velocity it was last sent, and pushed toward the
authority's pose through the solver as a critically damped spring — never by writing the transform,
because that is what makes networked physics look like objects teleporting through each other. Past
`HardSnapDistance` or `HardSnapAngle` it is teleported instead, which is the honest answer for a body
that respawned or whose owner dropped for a second.

⚠ **Two of those three thresholds were being read, and this section used to describe a system that
only had one and a half.**

- `HardSnapAngle` was declared, defaulted to π/2 and asked by nobody (#466), while the component's own
  remarks said a body far out is teleported. So a crate that ended up on a different face, or a
  vehicle the client had upside down, was spun towards the truth by the spring alone — over however
  long that took, through everything in the way. The angle now feeds the same snap list as the
  distance, measured by the same arithmetic the correction uses, so a body cannot snap by one measure
  and spin by the other.
- `NetworkRigidBody.IsResting` crossed the wire and was read by nobody (#465). A body the authority
  had declared asleep still had `offset × PositionStrength` added to its velocity every tick, so it
  was steered for ever by whatever quantisation error was left in its position — which is precisely
  the creep the flag exists to stop, paid for at one bit a body a tick. `SettledCount` is how many
  bodies were told to stop rather than steered; there is no other visible difference between a
  receiver that reads the flag and one that ignores it until the match has been running a while.

## Filling the ring

`LagCompensator.Track`, `Forget` and `Capture` are the honest primitives and they take a `BodyHandle`,
which a game gets out of a component it did not write. ⚠ **So nothing called them.** Until
[#515](https://github.com/Rikarin/Vixen/issues/515) the only file in the repository that constructed a
compensator was this package's own test, which means no rewind had ever run in a program and the ring's
memory shape, `ClampFor`'s clamping and the rewind/restore ordering had never met a frame.

`LagCompensationSystem` is the join. A server adds it once and tags what gets shot at:

```csharp
loop.Add(new LagCompensationSystem(compensator, session.Clock));
world.Add<LagCompensated>(pawn);
```

The system reconciles the ring against the world every tick — a tagged body joins, a body whose tag or
whose entity went away leaves — and captures after the physics writeback and before the replication
capture, which is where `Capture`'s own remarks say the history and the snapshot have to agree about
the instant. What stays the game's is the rewind, because only the game knows what a shot is.

⚠ **A capture during a rewind throws and is not caught.** The mistake is self-reinforcing: the ring
fills with the historical poses it just installed, and the result is a hit-registration bug that rots
for weeks. A `RewindScope` left undisposed is a bug in the game's shot code, and that is where the
stack trace should point.

## Owed

⚠ **Nothing in this repository runs any of this, and the list below is written under that.** Every
public system here — `NetworkRigidBodyCaptureSystem`, `NetworkRigidBodyCorrectionSystem`,
`LagCompensationSystem`, `PredictedPlayerMovement` — is constructed only by
`Core/Vixen.Net.Physics.Tests`; no sample, no editor path and no engine assembly adds one, and the
only mentions of the assembly outside its own tests are paragraphs like this one
([#1205](https://github.com/Rikarin/Vixen/issues/1205)). So the pose ring, the correction settle, the
rest detection and the prediction reconcile have each met a unit test and none of them a frame. It is
structural rather than an oversight: **no program in the tree has both networking and physics
bodies** — `Samples/08`, `09`, `10` and `14` reference `Vixen.Net` and none references
`Vixen.Physics`; `Samples/13` references `Vixen.Physics` and no networking; and
`Gameplay/Vixen.Gameplay.Shooting` deliberately references neither. The missing caller is a missing
*sample*, which is the decision under "A sample that shoots" below and not a line somebody forgot.

⚠ **But there is a rung between "the package wires it" and "no program runs it", and it is empty.**
`Vixen.Net.Physics` has **no registration surface at all** — no `AddNetworkPhysics`, no
`AddLagCompensation`, nothing of the `AddPhysics` / `AddAnimation` shape every other subsystem in this
engine is reached by. So the answer to "is this nothing-calls-it, or is the seam one level up?" is
*both*, and the second half is [#1254](https://github.com/Rikarin/Vixen/issues/1254): a game that
wanted networked physics **today** would hand-construct four systems, and would have to read the
source to learn that `NetworkRigidBodyCaptureSystem` must sit between the physics writeback and the
replication capture.

That knowledge is not actually loose — the systems carry `[UpdateInGroup]`, `[UpdateAfter]` and
`[UpdateBefore]` themselves, so a runner orders them correctly once they are in it — which is exactly
what makes the missing `Add…` cheap rather than a design question. ⚠ **And the sibling that already
took this step proves it does not close anything on its own**:
[#481](https://github.com/Rikarin/Vixen/issues/481) gave `Vixen.Net.Animation` an
`AddNetworkAnimation`, whose own remarks name this same gap — and swept over `*.cs` and `*.vxml`, its
only callers are still `NetworkAnimationWiringTests`. The seam is worth having and it is not the
missing caller; the program still is.

- **The hit-claim message itself.** This validates a claim; nothing yet defines one. A `[ServerRpc]`
  carrying tick, origin, direction and a claimed victim is the game's to declare, but the shape recurs
  enough that a `HitClaim` helper beside `NetworkTransform` would stop every game writing the same six
  lines — and the same argument was already made for `ReplicationChannel`.
- **Per-bone rewind.** The whole body moves as one, so a headshot is judged against a capsule rather
  than against a skeleton. That wants animation pose history, which is `Vixen.Animation`'s to keep, and
  it multiplies the ring by the number of tracked bones — worth doing deliberately rather than by
  extending this.
- **A backward-reconciliation budget.** Nothing bounds how many rewinds one connection can cause per
  tick beyond the RPC rate limit, and a rewind is more expensive than most calls. The rate limiter is
  the right place; it does not currently know that some calls cost more than others.
- **Drawing it.** `PhysicsDebugDraw` plus a history is exactly what "show me where the server thought
  everyone was" needs, and a disputed kill is unanswerable without it. `LagCompensator.TryGetHistory`
  says in its own doc comment that it is "for a diagnostic that wants to draw it", and its only caller
  is still a test.
- **A sample that shoots.** ⚠ `Samples/08-Multiplayer` *does* have a hitscan — `Arena.Resolve` — and
  #515 said it did not. What it has no trace of is physics: a fighter is a `NetworkTransform` and the
  hit test is a dot product, so there is nothing for a rewind to move. Wiring the compensator into it
  means giving the arena a `PhysicsScene` and bodies, which is a rewrite of the sample rather than a
  call, and the end-to-end behaviour is therefore still unmeasured in a program.
- **A registration surface**, per the paragraph above — filed as
  [#1254](https://github.com/Rikarin/Vixen/issues/1254). Not a substitute for the sample and not
  blocked by it.

⚠ **One correction to how #515 is usually restated.** "The package wires it" is true of the *call* and
not of the *object*: `LagCompensationSystem` calls `Capture` on a `LagCompensator` it is handed, and
`new LagCompensator(…)` appears nowhere outside `Core/Vixen.Net.Physics.Tests` — not even inside this
package. So there are three rungs here rather than two, and the middle one (a game builds the
compensator and hands it to the system) is the one an `AddNetworkPhysics` would have to make a
decision about.

## Predicted players

`PredictedPlayerMovement` is the `PredictedStep<PlayerMoveInput>` a client replays: write the tick's
input onto the pawn, step the physics scene, publish the transform. It is deliberately thin, because
`Vixen.Physics` already has the movement rules and a second implementation is a second implementation
that can disagree with the first.

It lives here for the reason this package exists at all — it is the only type that has to see both a
`PhysicsScene` and a `Tick`.

⚠ **Three things have to be true or the prediction reports perfect agreement while the two machines
drift apart.** All three were found by tests that failed for the right reason, and all three produce a
`MispredictionCount` of zero, which is the number you would otherwise trust.

| | |
|---|---|
| The tick must call `World.AdvanceVersion()` | `SystemRunner` does it at every phase boundary and nothing else does. A replayed tick has no frame loop, so without it every write is stamped with the previous tick's version and `WithChanged` matches nothing from the second tick onwards |
| The tick must publish `NetworkTransform` | Physics writes `LocalTransform`; the history and a snapshot both speak `NetworkTransform`. Publishing in the frame loop instead records the *previous* tick's pose |
| The rollback must reach into Jolt | A `CharacterController`'s position is native state no snapshot restores. `PhysicsScene` adopts a written `LocalTransform`, so a replay starts from the server's state rather than from the guess it was correcting |

The test that says it works is `MispredictionCountIsZeroOverALosslessRun` — a client and a server
stepping the same decoded inputs over 180 ticks with zero rollbacks — and the test that says *that*
test means anything is `ThePredictedStatePublishesWhatTheCharacterDid` beside it.
