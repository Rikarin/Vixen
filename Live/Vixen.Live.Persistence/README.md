# Vixen.Live.Persistence

Where a world's durable state lives. Accounts, characters, and the **append-only double-entry
journal** every movement of value is written to.

Spec: [docs/plan/27-mmo-framework.md](../../docs/plan/27-mmo-framework.md) § Persistence, ADR-021.

## Using it

```csharp
// The shipped one. The caller brings the driver — this assembly references none.
await using var store = new SqlPersistence(NpgsqlDataSource.Create(connectionString), ownsSource: true);

await store.MigrateAsync(cancellation);

// A trade is ONE intent, because a crash between its halves would be a lost sword.
var result = await store.Ledger.AppendAsync(
    new LedgerIntent {
        Key        = new(alice, "trade", auctionId),   // derived from the operation, never generated
        LeaseEpoch = lease.Epoch,                      // ADR-021, reaching the database
        At         = realm.Clock.Now,
        Detail     = "greatsword for 500g",
        Movements  = [
            new(LedgerAccount.Of(alice), sword, -1), new(LedgerAccount.Of(bob), sword, 1),
            new(LedgerAccount.Of(bob), gold, -500),  new(LedgerAccount.Of(alice), gold, 500)
        ]
    },
    cancellation
);

if (result.Ok) { … }        // Applied *or* Replayed — the caller cannot tell and must not care
```

## The three rules, and what each one is here

**Single writer per aggregate, enforced by a fence rather than by a lock.** `PlayerRecord.LeaseEpoch`
is the epoch of the last write the row accepted, and it only ever rises. A write below it comes back
`Superseded` — which is not an error: a realm that lost its lease mid-combat keeps simulating, its
buffered writes arrive late, and the database declines them without anybody having to notice in time.
The check is the `where` clause of the update, because reading the epoch and then writing would be the
same check with the race in the middle.

**Every movement of value is a ledger row, with an idempotency key derived from the operation.**
`(player, kind, operationId)` is a *primary key*, so a duplicate delivery loses the insert rather than
being remembered about by the application. A replay answers `Replayed` with the original's sequence
number — including when the balances have since moved past being able to afford it again, which is the
case a retry-after-the-fact has to survive.

**Grain state is coordination; gameplay is not.** `IPlayerGrain` persists its lease epoch through
Orleans grain storage and its inventory through here, because the support tool, the economy dashboard
and the analytics job all read these tables and none of them is a silo.

## The world has accounts, and that is what makes conservation checkable

Doc 27 says every movement of value is a row. It does not say what a loot drop moves value *from* —
and if the answer is "nowhere", every faucet and every sink is an exception to the sum-to-zero rule.
A rule with exceptions cannot be a constraint.

So a drop is a transfer out of `world/loot`, a vendor sale is a transfer into `world/vendor`, and the
invariant becomes total: **every intent's deltas sum to zero, per asset, always.** The cost is a few
named accounts whose balances go steadily negative — and `world/loot`'s balance is then exactly how
much of an asset has entered the economy, which is the number a dashboard is built to show and which
no other schema gives you free.

⚠ **A player account may not go negative and a world account must be allowed to.** An overdrawn
inventory is the duplication bug wearing a minus sign.

## Balances are a projection

`live_balance` is maintained in the same transaction as the rows behind it, so a read is a lookup
rather than a scan over a character's whole history. What makes a cached aggregate safe to believe is
`ReconcileAsync`, which is doc 27 § Testing's conservation oracle offered as an *operation* rather than
only as a test — a fleet that has been up for a month wants the answer CI gets after every randomised
transfer, and the answer being cheap is what makes a nightly job actually run it.

## There is no password field, and there is not going to be one

An engine that shipped a credential store would ship a liability its authors do not operate: hashing
parameters that age, breach response, reset, MFA, recovery. What the gate needs is *which account is
this*, and that comes from whatever the deployment already trusts. `AccountRecord.Handle` is what an
authority hands back; the seam is `IAccountAuthority` in `Vixen.Live.Gate`. Same position doc 16 took
on Steam and EOS transports, and doc 27 M-Q1 restated.

## What a test can and cannot say

`MemoryPersistence` is what every semantic here is asserted against on every push — this tier's
`Vixen.Net.Transport.Local`. The duplication oracle is four thousand operations across eight lanes with
duplicate deliveries, stale epochs and overdrafts mixed in, and it finishes in a hundred milliseconds;
against a database it would be a test nobody runs.

Whether PostgreSQL accepts the statements in `Schema` is a question only PostgreSQL answers, and it
belongs on the nightly leg beside `kind` and Docker. ⚠ `MemoryPersistence` is **not a deployment
target** — nothing in it survives the process.

## See also

- [docs/guide/live/durable-state](../../docs/guide/live/durable-state.md) — the written half.
- [`Vixen.Live.Cluster`](../Vixen.Live.Cluster/README.md) — `IPlayerGrain`, which owns the lease this
  fence is the database half of.
