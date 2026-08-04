---
title: Admission, health and the control plane
slug: live/admission-and-health
kind: guide
area: Live
summary: The door a ticketed player comes through, the two-second sample a fleet is watched by, and the one place a realm ever calls an orchestrator.
api: [T:Vixen.Live.Realms.PlayerAdmission, T:Vixen.Live.Realms.RealmPlayer, T:Vixen.Live.Realms.AdmissionRefusal, T:Vixen.Live.Realms.RealmDirectory, T:Vixen.Live.Realms.RealmHeartbeat, T:Vixen.Live.Realms.RealmHealth]
tags: [live, mmo, admission, diagnostics, threading]
since: 0.1
status: preview
related: [live/transfer-tickets, live/writing-a-realm]
---

## What it is

Three things a realm does that a listen server does not: check a ticket at the door
(`PlayerAdmission`), say every two seconds what it is costing (`RealmHeartbeat`, `RealmHealth`), and
ask an orchestrator questions without ever waiting for the answer (`RealmDirectory`).

## What it is for

### The door

`PlayerAdmission` is an `ISessionAuthenticator` — doc 16's existing seam, needing no new mechanism.
The session hands over whatever the client sent at the handshake, and this decides. What arrives is an
encoded `TransferTicket`; what comes back is accept-as-somebody, or one of five named refusals.

It is **synchronous and never answers `Pending`**, which is the property ADR-020 was designed for: the
ticket is self-contained and the cluster key is already in the process, so admission costs an HMAC
rather than a round trip.

`RealmPlayer` is the join between the two identities: `Key` is who the database thinks they are and is
the same on every realm they visit; `Id` is who this session numbers them as and means nothing
anywhere else.

### The heartbeat

A shard whose tick p99 exceeds its budget for a sustained window should stop being a placement
candidate *before* it stops being playable. That is the difference between a fleet that degrades and
one that falls over, and it needs a number the mean does not give: a shard averaging 4 ms with a p99
of 40 ms is one where every player sees a hitch twice a second.

⚠ **None of the numbers in `RealmHealth` is a second measurement system.** Every one is already an
instrument in `Vixen.Net.Telemetry`; the heartbeat is a *sample of the meter*, so a shard's health and
its traces cannot disagree about what its tick cost.

### The control plane

**Orleans is asked, not awaited.** A grain call is a network round trip with a scheduler in front of
it: a frame that awaits one has a p99 measured in milliseconds and a p99.9 measured in seconds. So a
realm posts a request, keeps simulating, and applies the answer at a defined point in a later frame.

This is not a new pattern here. `ISessionAuthenticator` is already shaped exactly this way — answering
`Pending` and being asked again next update — and doc 16 recorded why: a completion on a thread-pool
thread would make every layer it touches thread-safe for the sake of an event that happens twice a
minute.

## Using it

```csharp no-compile="the grain interface this would call is milestone L1"
directory.Ask(
    cancellation => playerGrain.AcquireLeaseAsync(epoch, cancellation),   // off the realm's thread
    lease => player.Lease = lease,                                        // on the realm's thread
    failure => log.LeaseFailed(failure));
```

⚠ **The `apply` callback runs on the realm's thread, inside `Drain`.** That is the entire value of the
type: everything it touches — the world, the session, the admission list — is single-threaded and
stays that way. The call delegate runs wherever the task ran and must touch none of them.

⚠ **Nothing in `RealmDirectory` knows what a grain is.** L0 has no orchestrator and the class is
already the right shape, because what it enforces is the threading discipline rather than the
transport.

`Pending` is worth a metric rather than only a field: a number that climbs and does not come down is
what a control plane that has stopped answering looks like from inside a realm — which is otherwise
perfectly happy, because it is still simulating.

## Examples

### The five refusals

```csharp no-compile="what the door answers; RealmHost installs it for you"
AdmissionRefusal.NoTicket      // nothing was presented, or it was not a ticket
AdmissionRefusal.BadTicket     // it did not survive TransferTicketSigner.Validate
AdmissionRefusal.Full          // the shard is at its hard cap
AdmissionRefusal.Draining      // it takes no arrivals at all
AdmissionRefusal.AlreadyHere   // that character already has a session on this shard
```

`AlreadyHere` is a refusal rather than a replacement, and the difference matters: a second session for
one character is either a transfer that has not finished or an attempt at duplication, and in both the
safe answer is that the player stays where they already are — the same asymmetry the transfer protocol
has, where every abort leaves them somewhere valid.

Refusals are counted by reason. A shard refusing everybody because its clock disagrees with the
orchestrator's presents as "nobody can join" and is diagnosed in one glance from a histogram of
`BadTicket`.

### The sample

```csharp no-compile="RealmHost raises these; a game subscribes"
host.Sampled += health => log.Shard(health.Population, health.TickP99Milliseconds, health.Blocked);
```

`Blocked` is how many players a drain could not move, which is the input to doc 27's escalation: a
shard that reaches its hard deadline with blocked players raises a live-ops alert rather than killing
anybody.

Nothing in the realm decides what to do with a sample. A realm that judged its own shard unhealthy
would be a second opinion in a system whose whole design is that exactly one place decides a given
question.

## See also

- [Transfer tickets](transfer-tickets) — what the door is checking.
- [Writing a realm](writing-a-realm) — what installs all three.
- [docs/plan/27](https://github.com/Rikarin/Vixen/blob/master/docs/plan/27-mmo-framework.md) § ADR-016, § Health.
