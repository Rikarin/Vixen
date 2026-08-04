---
title: Transfer tickets
slug: live/transfer-tickets
kind: concept
area: Live
summary: A player's signed, expiring permission to be admitted to a shard — and why the client can only carry it.
api: [T:Vixen.Live.TransferTicket, T:Vixen.Live.TransferTicketSigner, T:Vixen.Live.TicketStatus, T:Vixen.Live.TransferReadiness]
tags: [live, mmo, security, transfer]
since: 0.1
status: preview
related: [live/admission-and-health, live/shards-and-specs]
---

## What it is

A `TransferTicket` says: *this character may enter that shard, at that lease epoch, until that
moment*. It is signed with a key every realm in a cluster holds and no client ever does, so the client
that carries it is a courier — it can neither read anything it did not already know nor forge one.

`TransferTicketSigner` mints them and is the only thing that can tell a real one from a made-up one.
`TicketStatus` is what it decided.

## What it is for

This is `NetworkSession`'s reconnect token with a different issuer. Doc 16 already established
server-issued, opaque, expiring tokens that let a `PlayerId` survive a dropped `ConnectionId`; a
transfer ticket is the same object minted by the orchestrator instead of by the source session.

Two properties make the transfer protocol work:

- **Admission costs an HMAC, not a round trip.** The ticket is self-contained and the key is already
  in the realm's process, so the second session a transfer opens is admitted in the time it takes to
  hash a hundred bytes. That is what lets it overlap with the player still playing on the first
  realm, which is the whole reason a map change is a preload rather than a reconnect.
- **A replayed ticket is harmless.** It names a lease epoch, and an epoch already superseded is a
  no-op rather than a second grant. The expiry is a second, cruder bound on the same window.

## Using it

```csharp no-compile="the orchestrator that mints these is milestone L1"
using var signer = new TransferTicketSigner(clusterKey);       // ≥ 32 bytes, or it refuses

var ticket = signer.Sign(new TransferTicket {
    Player     = player,
    Target     = shard,
    Endpoint   = endpoint,
    LeaseEpoch = epoch + 1,
    Expires    = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30)
});

var carried = ticket.Encode();     // what the client is handed, and all it is handed
```

and on the realm being entered:

```csharp no-compile="what PlayerAdmission does with a handshake payload"
if (!TransferTicket.TryDecode(presented, out var ticket, out var why)) {
    return AuthenticationDecision.Refuse("A ticket is required.");
}

var status = signer.Validate(ticket, myShard, DateTimeOffset.UtcNow);
```

⚠ **Decoding is not validating.** `TryDecode` answers whether the bytes were a ticket, which is a
question about a stranger's input. Whether it is *this cluster's* ticket, not yet expired, for the
shard being entered is `Validate`, and that is the only check that means anything.

`Validate` checks signature, then expiry, then shard, in that order — so everything after the first
check is a statement about a ticket this cluster actually issued.

## Examples

Every refusal has a name, and the names are told to the client on purpose: *expired* is something it
can act on by asking the gate for another, and *wrong shard* by asking where it should have gone.
Neither tells an attacker anything the ticket did not.

```csharp no-compile="illustrates the five outcomes of a check against a stranger's input"
signer.Validate(unsigned,          shard, now);   // TicketStatus.Unsigned
signer.Validate(anotherClusters,   shard, now);   // TicketStatus.Forged
signer.Validate(lastWeeks,         shard, now);   // TicketStatus.Expired
signer.Validate(forTheShardNextDoor, shard, now); // TicketStatus.WrongShard
```

### The key

⚠ **The cluster key is the whole of the security of admission.** Anyone holding it can admit anyone
to anything. It belongs in whatever the deployment already uses for secrets, and never in a
`RealmSpec` — a spec travels on a command line, and a command line is visible to every other process
on the machine.

A realm nobody handed a key to derives one from its own spec (`RealmHost.DevelopmentSigner`). That is
a development convenience and is not meant to look like a security mechanism: what it buys is that a
deployment which forgot to configure a key gets a fleet that refuses everybody — which is loud —
rather than one that admits anybody, which is not.

### Readiness is the other half

`TransferReadiness` is what a *draining* shard asks about each player before moving them: `Ready`,
`Soon`, or `Blocked`. The engine ships a default that says everybody, always, and does not pretend to
know better — "in a scripted encounter" is a sentence only the game can finish. Nothing is
force-disconnected by a drain; `Blocked` escalates to a live-ops alert at the hard deadline, and that
path ends in a human rather than in a kick.

## See also

- [Admission and health](admission-and-health) — the door that checks these.
- [Shards, keys and specs](shards-and-specs) — what a ticket names.
- [docs/plan/27](https://github.com/Rikarin/Vixen/blob/master/docs/plan/27-mmo-framework.md) § ADR-020, § ADR-021, § Drain.
