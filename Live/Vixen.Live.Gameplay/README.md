# Vixen.Live.Gameplay

The join between [doc 28](../../docs/plan/28-gameplay-framework.md)'s gameplay libraries and
[doc 27](../../docs/plan/27-mmo-framework.md)'s persistence.

Spec: doc 27 § Persistence and ADR-021, doc 28 § Economy.

## State

**Built: the identity join, and the economy on the ledger. 20 tests.**

| | |
|---|---|
| `IGameplayIdentity` · `GameplayIdentityMap` | Which durable player a gameplay rule means. |
| `LedgerBridge` · `PendingWrite` · `BridgeRefusal` | `IEconomyLedger` in the frame, `ILedger` afterwards. |

**Owed:** the `PlayerRecord.Profile` codecs, the checkpoint policy, and durable `IPityStore`,
`ISocialStore` and `ILockoutStore` — task **#39**.

## This assembly can only be here

`Live/` may reference `Gameplay/` and nothing in `Gameplay/` may reference `Live/`
(`Build.ArchitectureRules.cs`). So the join has exactly one legal home, and before this it did not
exist at all: `Vixen.Live.Realm` did not reference `Vixen.Live.Persistence`, and there was no path
from a realm to the ledger.

## Three player identities, and only two were joined

| | | |
|---|---|---|
| `Vixen.Net.Sessions.PlayerId` | `uint` | what the session assigns |
| `Vixen.Live.Abstractions.PlayerKey` | two `Guid`s | what the database is keyed by |
| `Vixen.Gameplay.PlayerId` | `ulong` | what every rule in doc 28 passes around |

`RealmPlayer` already joined the first two. Nothing joined the third, and it is the one every durable
write starts from.

⚠ **The join is a widening, not a hash.** A gameplay id *is* its session id in a wider integer.
Hashing a 256-bit `PlayerKey` into 64 bits collides, and two players who collided would write each
other's inventory.

⚠ **A gameplay `PlayerId` is realm-scoped and must never reach the database.** Doc 28 says one is
*"stable for as long as a party invite or a mute list has to be"* — and those are not the same
length. A party lasts a session; a mute list outlives every realm the player will ever be on. A saved
row carrying a raw gameplay id means somebody else next week.

## The economy, applied here and written down later

`IEconomyLedger` is synchronous because a rule calls it mid-frame, several times per hit. `ILedger` is
a database round trip. **ADR-016's rule makes a blocking adapter between them the one implementation
that is definitely wrong, and it is also the obvious one.**

So the in-memory projection is authoritative *for the frame* and the database is the audit trail.
That is not optimism: ADR-021's lease says exactly one realm may write a player, so the two cannot
disagree while the lease is held. Accepted intents go into an outbox; the realm drains it and posts
through `RealmDirectory`; the answer arrives at a later `PreUpdate`.

⚠ **A drained write is not removed.** In flight is not done, and losing a ledger intent is losing an
item. `Settle` is what removes one.

⚠ **`Superseded` is not an undo.** ADR-021: a realm that loses its lease *"keeps simulating, buffers
durable mutations as ledger intents, and either flushes them when the lease returns or hands them to
the new holder"*. Rolling the projection back would take an item off somebody still holding it. When
the lease returns, every waiting write is **restamped** — one naming the dead epoch is declined by
the same fence, for ever.

⚠ **`Insufficient` or `Unbalanced` from the database is a defect, not a refusal.** The projection
checked both before the intent was queued, so the database disagreeing means the single-writer
property has been broken. Counted, raised on `Diverged`, and kept in the outbox as evidence — a
bridge that swallowed those would lose items unreproducibly.

⚠ **A saved balance is seeded from the database's balances, never replayed from its journal.** A
replay re-runs every intent since the account was made: slow for a year-old character, and a second
chance to get it wrong. The seed comes out of a world account so the projection's own conservation
holds.

## See also

- [`Vixen.Live.Persistence`](../Vixen.Live.Persistence/README.md) — the ledger and the repositories.
- [`Vixen.Gameplay.Economy`](../../Gameplay/Vixen.Gameplay.Economy/README.md) — the other side.
