# Vixen.Live.Gameplay

The join between [doc 28](../../docs/plan/28-gameplay-framework.md)'s gameplay libraries and
[doc 27](../../docs/plan/27-mmo-framework.md)'s persistence.

Spec: doc 27 § Persistence and ADR-021, doc 28 § Economy.

## State

**Built: the identity join, the economy on the ledger, the profile container, the checkpoint policy
and a durable pity store. 44 tests.**

| | |
|---|---|
| `IGameplayIdentity` · `GameplayIdentityMap` | Which durable player a gameplay rule means. |
| `LedgerBridge` · `PendingWrite` · `BridgeRefusal` | `IEconomyLedger` in the frame, `ILedger` afterwards. |
| `PlayerProfile` · `ProfileSectionId` | `PlayerRecord.Profile`'s contents, as named slices. |
| `IProfileSection` · `ProfileBinder` · `ProfileSections` | The seam a game registers its codecs with. |
| `CheckpointPolicy` · `CheckpointReason` | When counters are written down. |
| `ProfilePityStore` | Doc 28's durable pity counters, as a section. |

**Owed:** durable `ISocialStore` and `ILockoutStore` over their grains, and the remaining section
codecs — task **#39**.

## Assets go in the ledger; counters go in the profile

The rule the whole assembly is arranged around, and it decides the storage on its own. An asset can
be **duplicated** — gold, items — so every movement of one is an append-only row with an idempotency
key. A counter cannot: writing level 42 twice leaves you at 42. So a counter is written on a
**cadence**, and a crash loses at most one interval of it.

⚠ **That loss is the correct trade rather than a compromise.** Making a counter durable per kill puts
a database round trip on the combat path, which ADR-016 forbids outright.

It is also why `ProfilePityStore` can be synchronous where `LedgerBridge` could not: a loot roll asks
for a count mid-frame and the answer is in memory, and what makes it durable is the checkpoint
underneath rather than a round trip at the call.

## The profile keeps what it does not understand

⚠ **An unknown section is preserved, never dropped**, and that is why the container knows nothing
about types. Doc 27 § Upgrades fragments a population by version *on purpose*, so during a rollout an
old realm and a new realm both write the same character. An old realm that dropped the section the
new one added would lose it — silently, and only for players who zoned the wrong way. A map of id to
bytes cannot make that mistake; the codecs that know types sit above it.

⚠ **Two sections on one id are refused rather than last-wins**, which would be one of them silently
reading the other's bytes.

⚠ **Sections are written in id order and identical bytes are not a change**, or every checkpoint
looks like one and the row is rewritten on a cadence for ever.

⚠ **A failed checkpoint stays dirty *and* does not restart the clock.** Clearing the flag loses the
interval for good; restarting the clock turns a five-second outage into five minutes of lost
progress.

⚠ **Transfer and logout write only when there is something to write.** "Always on transfer" reads as
unconditional and should not be: a character nobody changed has the same bytes stored already, and
the round trip to say so is one spent inside L2's overlap window.

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
