---
title: Transferring players
slug: live/transferring-players
kind: concept
area: Live
summary: The overlap — a second session opened while the player is still playing on the first, and the lease epoch that makes the switch atomic.
api: [T:Vixen.Live.Transfer.SourceTransfer, T:Vixen.Live.Transfer.ClientTransfer, T:Vixen.Live.Transfer.ClientTransferState, T:Vixen.Live.Transfer.TransferBoard, T:Vixen.Live.Transfer.Arrival, T:Vixen.Live.Transfer.ArrivalState, T:Vixen.Live.Transfer.ReservationRefusal, T:Vixen.Live.Transfer.TransferPhase, T:Vixen.Live.Transfer.TransferAbort, T:Vixen.Live.Transfer.TransferPrepare, T:Vixen.Live.Transfer.TransferCommit, T:Vixen.Live.Transfer.RealmHandoff, T:Vixen.Live.Transfer.TransferDeadlines, T:Vixen.Live.Transfer.TransferMetrics, T:Vixen.Live.Transfer.TickRebase]
tags: [live, mmo, transfer, seamless]
since: 0.1
status: preview
related: [live/transfer-tickets, live/admission-and-health, live/placing-players]
---

## What it is

A player walking from one map to another is a **transfer**: their client opens a second session to
the realm they are going to *while the first one is still authoritative*, loads the map, and switches
at a tick both realms agree on.

Three pieces. `SourceTransfer` is the state machine on the realm that still owns them and is the only
thing that can decide nothing happened. `TransferBoard` is the receiving realm's held slots.
`ClientTransfer` is the client's two sessions and its clock.

## What it is for

Nothing migrates a socket, forwards packets or proxies a connection — all three need an intermediary
that outlives the realm, which is the gateway doc 27 spends a page rejecting. A second session costs
one handshake, already built and already fuzzed, and it is *overlapped* with the player continuing to
play, so its latency is hidden rather than paid.

**The loading screen is a preload.** t3 is where the map is fetched, and it happens while the player
is still walking around on the source. For cached content it is invisible; for a first visit it is a
progress bar that runs during play.

## Using it

On the realm they are leaving:

```csharp no-compile="the grain calls go through RealmDirectory — ADR-016's rule is about where this is driven from"
var transfer = new SourceTransfer(player, "maps/divinity", clock.Now, reason: "a portal");

// t1: the orchestrator answered. t2: the target holds a slot.
transfer.Placed(result.Shard, prepare, epoch, clock.Now);
transfer.TargetReady(clock.Now);

// … the player keeps playing here for the whole of t3 …

transfer.ClientReady(clock.Now, atTick: clock.Tick + 30);   // the CLIENT reports this, not the target
transfer.LeaseTaken(granted.Epoch, clock.Now);              // the atomic moment
transfer.HandoffAcknowledged(clock.Now);                    // t6 — and only now do we despawn
```

⚠ **`ClientReady` is reported by the client, not by the target.** The target knows it admitted
somebody; only the client knows whether its own map finished loading and its first snapshot arrived.
Moving a player whose target is still a loading screen is the one thing the overlap exists to prevent.

Once per update, on both realms:

```csharp no-compile="continues the snippet above"
transfer.Step(clock.Now);          // gives up on whatever ran out of time
board.Sweep(clock.Now);            // drops the slots nobody came for
```

## Examples

**Deciding whether there is room** — pending arrivals are capacity that is already spent:

```csharp no-compile="what a realm does when the orchestrator asks it to expect somebody"
var room = host.Population + board.Pending < spec.Capacity.HardCap;

var refusal = board.Reserve(ticket, epoch, clock.Now, room, host.State == ShardState.Draining);
```

**Every abort leaves the player where they were:**

```csharp no-compile="the source's own update"
if (transfer.Step(clock.Now)) {
    metrics.Record(transfer);
    log.TransferAborted(transfer.Player, transfer.Abort);
    // Nothing else. They are still here, still simulated, still holding their lease.
}
```

⚠ **Aborting a committed transfer is refused rather than tolerated.** `Stop` returns `false` once
`Phase` is `Committed` — a source that "un-committed" would claim a player two realms now believe in.

**What the client pays**, and it is one line:

```csharp no-compile="at t6"
client.Committed(commit);          // PredictionResets++ — exactly one per transfer, at the switch
```

## The honest cost

The two realms run independent clocks and the two are not related. `TickRebase` is the whole of the
relationship, measured across the overlap so it has converged by the time it is used.

What cannot be carried over: `ClientPrediction`'s history is cleared, because rolling back across a
realm boundary means replaying against a simulation that no longer owns this player; `InputLog` is
cleared and re-armed from the target's first snapshot; `SnapshotBuffer`s are dropped and motion holds
for one interpolation delay.

**So a transfer costs one interpolation delay of extra smoothing and one prediction reset** — roughly
100–150 ms of softer local response, once, at a moment the player initiated. That is the price, and
`TransferMetrics` reports `OverlapDuration`, `CommitLatency`, `PredictionResetCount` and the abort
histogram because a transfer that degrades is one that stops being seamless quietly.

## See also

- [Transfer tickets](transfer-tickets.md) — the signed permission this protocol carries.
- [Admission and health](admission-and-health.md) — what the receiving realm does at the door.
- [docs/plan/27](https://github.com/Rikarin/Vixen/blob/master/docs/plan/27-mmo-framework.md)
  § Transfer — the seven timestamps, and the five properties each of them protects.
