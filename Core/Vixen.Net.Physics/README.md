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

## Owed

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
  everyone was" needs, and a disputed kill is unanswerable without it.
