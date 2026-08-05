---
title: Durable state and the ledger
slug: live/durable-state
kind: concept
area: Live
summary: Accounts, characters and the append-only double-entry journal every movement of value is written to — and the fence that makes one writer one writer.
api: [T:Vixen.Live.Persistence.IPersistence, T:Vixen.Live.Persistence.ILedger, T:Vixen.Live.Persistence.IPlayerRepository, T:Vixen.Live.Persistence.IAccountRepository, T:Vixen.Live.Persistence.IGuildRepository, T:Vixen.Live.Persistence.GuildRow, T:Vixen.Live.Persistence.GuildMemberRow, T:Vixen.Live.Persistence.LedgerIntent, T:Vixen.Live.Persistence.LedgerEntry, T:Vixen.Live.Persistence.LedgerAccount, T:Vixen.Live.Persistence.AssetMovement, T:Vixen.Live.Persistence.IdempotencyKey, T:Vixen.Live.Persistence.AssetId, T:Vixen.Live.Persistence.LedgerVerdict, T:Vixen.Live.Persistence.LedgerResult, T:Vixen.Live.Persistence.LedgerQuery, T:Vixen.Live.Persistence.LedgerDiscrepancy, T:Vixen.Live.Persistence.AccountRecord, T:Vixen.Live.Persistence.PlayerRecord, T:Vixen.Live.Persistence.WriteOutcome, T:Vixen.Live.Persistence.MemoryPersistence, T:Vixen.Live.Persistence.SqlPersistence, T:Vixen.Live.Persistence.Schema, T:Vixen.Live.Persistence.Schema.Migration]
tags: [live, mmo, persistence, ledger, economy]
since: 0.1
status: preview
related: [live/placing-players, live/transfer-tickets]
---

## What it is

`IPersistence` is four things behind one connection: `Accounts`, `Players`, `Guilds` and `Ledger`.

The first three are ordinary rows. The last is a **journal**: append-only, double-entry, and the only
place a quantity of anything ever changes. A character's gold is not a column — it is the sum of the
rows that moved it, and `live_balance` is a projection of those rows kept in the same transaction so
that reading it is a lookup.

Two implementations. `MemoryPersistence` is what the tests run against; `SqlPersistence` is what ships.

## What it is for

An MMO's unrecoverable failure is item duplication, and every game that got it wrong got it wrong the
same way: state serialised into a message, a packet lost, a retry, two copies. Three mechanisms here
make that unrepresentable rather than unlikely.

**The fence.** `PlayerRecord.LeaseEpoch` is the epoch of the last write the row accepted, and it only
ever rises. A realm that lost its lease mid-combat keeps simulating and keeps flushing; every one of
those late writes comes back `Superseded`, and nothing anywhere had to notice in time. This is
ADR-021's lease reaching the database — the grain decides who holds it, the fence enforces it.

**The idempotency key.** `(player, kind, operationId)` is a primary key, and it is *derived from the
operation* rather than generated per attempt. A key minted fresh on the retry is a different key, so
the retry is a second trade — which is the bug. Derived from the auction's id, the mail's id, the
quest's id, both attempts compute the same value and the second one loses the insert.

**Double entry.** Every intent's deltas sum to zero, per asset, always. There are no faucets outside
the ledger: a drop is a transfer out of `world/loot` and a sale is a transfer into `world/vendor`, so
the invariant is total and `ReconcileAsync` can check the whole database against itself.

## Using it

An intent applies whole or not at all, which is why a trade is one of them and not two:

```csharp no-compile="the grain that holds this lease is milestone L1's, and doc 28 owns what an item is"
var result = await store.Ledger.AppendAsync(
    new LedgerIntent {
        Key        = new(alice, "trade", auctionId),
        LeaseEpoch = lease.Epoch,
        At         = clock.Now,
        Detail     = "greatsword for 500g",
        Movements  = [
            new(LedgerAccount.Of(alice), sword, -1), new(LedgerAccount.Of(bob), sword, 1),
            new(LedgerAccount.Of(bob), gold, -500),  new(LedgerAccount.Of(alice), gold, 500)
        ]
    },
    cancellation
);
```

⚠ **`Applied` and `Replayed` are both success, and the caller must not distinguish them.** The whole
point of the idempotency key is that the caller cannot tell whether its first attempt reached the
database and does not have to:

```csharp no-compile="continues the snippet above"
if (!result.Ok) {
    // Unbalanced is a bug in the caller. Superseded is a transfer that already happened.
    // Insufficient is a player who does not have it.
    log.LedgerRefused(result.Verdict, result.Detail);
}
```

The shipped store takes a `DbDataSource` rather than a driver, so the deployment brings Npgsql and
configures pooling, TLS and tracing where it already configures them:

```csharp no-compile="Npgsql is the deployment's dependency, not the engine's"
await using var store = new SqlPersistence(NpgsqlDataSource.Create(connectionString), ownsSource: true);

await store.MigrateAsync(cancellation);
```

## Examples

**A loot drop is a transfer, not a creation.**

```csharp no-compile="what a realm does when a boss dies"
await store.Ledger.AppendAsync(
    LedgerIntent.Transfer(
        new(who, "loot", $"{encounterId}:{slot}"),      // two players looting one chest, two keys
        lease.Epoch,
        clock.Now,
        LedgerAccount.Of(LedgerAccount.Loot),
        LedgerAccount.Of(who),
        new("items/greatsword"),
        1
    ),
    cancellation
);
```

**The support tool's question.** *"What happened to my sword?"* is a query, and it is the reason the
asset is an addressable address rather than a number the database minted:

```csharp no-compile="doc 27 § Diagnostics' ledger query"
var rows = await store.Ledger.HistoryAsync(
    new() { Account = LedgerAccount.Of(who), Asset = new("items/greatsword"), Limit = 50 },
    cancellation
);
```

**The conservation oracle, as an operation.** Empty is the healthy answer, and a nightly job that runs
this is worth more than the test that also does:

```csharp no-compile="what `vixen live` will call"
foreach (var wrong in await store.Ledger.ReconcileAsync(cancellation)) {
    log.LedgerDiscrepancy(wrong.Account, wrong.Asset, wrong.Stored, wrong.Journalled);
}
```

### A guild's roster is rows, not a blob

⚠ **That is the whole reason `IGuildRepository` is a repository rather than grain storage.** § Persistence's
own rule is that gameplay data kept in Orleans's serializer *"cannot be queried by anything else —
including the support tool, the economy dashboard and the analytics job"*, and *"which guilds is this
account in"* is a query. So `GuildRow` carries `ImmutableArray<GuildMemberRow>` and the members are a
table.

⚠ **The fence is a *revision*, not a lease epoch**, and the difference is `IGuildGrain`'s. A
character is fenced by ADR-021 because two realms can each believe they hold it; a guild has no lease
at all, because its single writer is the grain's own turn. What the revision guards against is a
stale writer after a reactivation — optimistic concurrency rather than a lease. It travels on the row
and the comparison is in the same statement as the write, because reading it first would be the same
check with the race in the middle.

⚠ **`ForAccountAsync` is keyed by the account and not by the character**, because the question a
player asks is *"what am I in"* and the roster stores a `PlayerKey` whose account half is exactly
that.

⚠ Two of its methods are **explicit interface implementations** in both `MemoryPersistence` and
`SqlPersistence`, and it is not a style choice: `ReadAsync(Guid)` and `ForAccountAsync(Guid)` collide
with the account and player repositories' methods, differing only in return type.

⚠ **A `GuildRow` hand-writes its equality**, and so does `GuildMemberRow`'s container. A record's
generated equality compares an `ImmutableArray` **by reference**, so a row read back never equals the
one written — the trap doc 27 § Slice two already recorded for `RealmEndpoint`, hit again.

## What does not go here

**Anything that is a quantity of an asset does not belong in `PlayerRecord.Profile`.** Inventory and
currency are balances, balances are a projection of the journal, and a gold count in the profile blob
as well would be two numbers meaning one thing. The rule that decides: if the support tool would ever
be asked *where did this come from*, it is a ledger asset. If the answer is only ever *the player
chose it* — appearance, keybinds, quest flags, the position they logged out at — it is profile.

**Credentials do not go here at all.** `AccountRecord` has a handle and no password: what an engine
can honestly own is the mapping from *whatever your authority calls this person* to the account the
world knows. Everything upstream of that — OIDC, Steam, EOS, your own account service — is the
deployment's, and the seam is `IAccountAuthority`.

**A counterparty whose lease this realm does not hold does not go in an intent.** The fence checks the
*acting* character, so a face-to-face trade — both characters on one realm — may name both, and
anything else moves through `world/escrow`: value out under the sender's lease and their key, value in
under the recipient's. That is what makes mail and auctions safe across realms without a distributed
transaction.

## See also

- [Placing players](placing-players.md) — the lease this fence is the database half of.
- [Transfer tickets](transfer-tickets.md) — the other half of ADR-021's atomic moment.
- [docs/plan/27](https://github.com/Rikarin/Vixen/blob/master/docs/plan/27-mmo-framework.md)
  § Persistence — the three rules, and the failure mode behind each.
