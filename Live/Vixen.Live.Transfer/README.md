# Vixen.Live.Transfer

The handoff protocol, both sides of it. Everything in doc 27 that says *"seamless"* is this project.

Spec: [docs/plan/27-mmo-framework.md](../../docs/plan/27-mmo-framework.md) § Transfer, ADR-020, ADR-021.

## The shape

```
t0  SourceTransfer(player, "maps/divinity", now)          Placing
t1  .Placed(shard, prepare, epoch, now)                   Preparing    ← the orchestrator answered
t2  .TargetReady(now)                                     Overlapping  ← a slot is held; client told
t3      ClientTransfer.Connected() / .Loaded()                         ← two sessions. STILL PLAYING
t4  .ClientReady(now, atTick)                             Committing
    .LeaseTaken(epoch, now)                               HandingOff   ← the atomic moment
t5  .HandoffAcknowledged(now)                             Committed
t6      ClientTransfer.Committed(commit)                                ← one prediction reset
```

## Three properties, and each is a failure designed out

**`StillOurs` is true in every phase but the last.** The source keeps simulating the player through
the whole overlap, which is what makes a map change a preload instead of a loading screen. A realm
that stopped at t2 would give them three minutes of standing still while their map downloaded.

**Every abort leaves them exactly where they were.** `NoShard`, `TargetNeverReady`,
`ClientNeverArrived`, `TicketExpired`, `HandoffLost`, `LeaseLost`, `PlayerGone`, `Cancelled` — all
eight end with the player playing on the source, because the source never commits until the target has
acknowledged. ⚠ **Aborting after `Committed` is refused rather than tolerated**: a source that
"un-committed" would claim a player two realms now believe in, which is the duplication this design
has no other way to express.

**A lease taken at an unexpected epoch aborts the transfer.** A third realm acquiring means ADR-021's
fence would refuse every durable write this transfer makes, so continuing into a handoff whose durable
half can never land is worse than stopping.

## The reservation is capacity spent before anybody connects

`TransferBoard` holds a slot from t1. Without it, a map at 99 % could promise the same last slot to
twenty players in flight and refuse nineteen at the door — each *after* loading the map. The
reservation is what makes `PlaceStatus.Placed` a promise rather than a guess.

⚠ It therefore has to expire, and `Sweep` is what a realm calls once per update. A reservation whose
ticket has expired goes with it: the client can no longer be admitted, so the slot is being held for
nobody.

**Dormancy is what stops the player existing twice.** Between t3 and t5 they have a session here and
are receiving interest so their client can load — with no ownership, no input and no camera. A target
that spawned them live at t3 would put two of them in the world for the length of the overlap.

## What the client pays, stated rather than hidden

The two realms' clocks are unrelated. `TickRebase` is the whole of the relationship — an offset,
measured across the overlap so it has converged by t6, which makes the switch a pointer change rather
than a resync.

What cannot be carried: the prediction history is meaningless across a realm boundary, the input log
is re-armed from the target's first snapshot, and the snapshot buffers are dropped. **So the visible
cost is one interpolation delay of extra smoothing and one prediction reset** — 100–150 ms of softer
local response, once, at a moment the player initiated. `TransferMetrics` reports it, because a
transfer that degrades is one that stops being seamless quietly.

## What a test can say

Everything. All 47 run in 35 ms with no realm, no cluster and no socket: the protocol is three state
machines fed events and asked what to do next, which is the same shape `ShardLifecycle`,
`PlayerLeaseState` and `GateService` took. § Testing asks for *every abort path, injected*, and nobody
injects a source realm dying at t5 against three live processes.

⚠ **What is here is the protocol, not the payload.** `RealmHandoff` carries encoded components and
this assembly does not encode them — that is the replication codec's job and the next slice's.

## See also

- [docs/guide/live/transferring-players](../../docs/guide/live/transferring-players.md) — the written half.
- [`Vixen.Live.Abstractions`](../Vixen.Live.Abstractions/README.md) — `TransferTicket`, the permission
  this protocol carries.
