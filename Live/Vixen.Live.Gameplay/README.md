# Vixen.Live.Gameplay

The join between [doc 28](../../docs/plan/28-gameplay-framework.md)'s gameplay libraries and
[doc 27](../../docs/plan/27-mmo-framework.md)'s persistence.

Spec: doc 27 § Persistence and ADR-021, doc 28 § Economy.

## State

**Built: the identity join, the economy on the ledger, the profile container, the checkpoint policy,
a durable lockout store, a durable social store and all five profile section codecs. 102 tests.**

| | |
|---|---|
| `IGameplayIdentity` · `GameplayIdentityMap` | Which durable player a gameplay rule means. |
| `LedgerBridge` · `PendingWrite` · `BridgeRefusal` | `IEconomyLedger` in the frame, `ILedger` afterwards. |
| `PlayerProfile` · `ProfileSectionId` | `PlayerRecord.Profile`'s contents, as named slices. |
| `IProfileSection` · `ProfileBinder` · `ProfileSections` | The seam a game registers its codecs with. |
| `CheckpointPolicy` · `CheckpointReason` | When counters are written down. |
| `ProfilePityStore` | Doc 28's durable pity counters, as a section. |
| `ProgressionSection` · `QuestSection` · `ExplorationSection` · `WardrobeSection` | The other four codecs. |
| `LockoutBridge` · `PendingLockout` | `ILockoutStore` in the frame, `IInstanceGrain` afterwards. |
| `SocialBridge` · `GuildEdit` · `PendingGraph` · `SocialLink` | `ISocialStore` in the frame, `IGuildGrain` afterwards. |

**Owed:** nothing. #39 closed the last of it.

## A sync store over async truth has a cold-read problem the interface cannot express

`LedgerBridge`, `LockoutBridge` and `SocialBridge` are the same shape for the same reason, and the
differences between them are worth stating because two of the three are dangerous.

⚠ **An unknown balance reads as zero and refuses a purchase** — annoying, and safe. **An unknown
lockout reads as `null`, which `ILockoutStore.Find` defines as "not locked"** — so a player whose
lockouts have not been loaded is admitted to a raid they are already saved to, and the run they get
is one the fleet cannot take back. Doc 28's whole reason for making lockouts fleet-wide is that *"a
lockout one shard knew about is a lockout a player evades by zoning"*, and a cold cache is that hole
reopened from inside.

The interface has no way to say *"I do not know"*, so the mistake is **counted** instead: `IsWarm` is
what admission checks, and a `Find` for somebody never warmed increments `ColdReads` and raises
`Cold`. That is `LedgerBridge.Divergences`' posture — a wrong answer the type cannot prevent is made
loud rather than quiet.

⚠ **Warming with an empty list is meaningful.** "Saved to nothing" and "nobody has asked" are the
same absence in a cache and must not be the same fact.

⚠ **Purging the view is not releasing a lockout.** It drops what has lifted from a realm's memory;
what decides it has lifted is the reset the cluster holds, and a realm that could write a release
would be one that ends a raid lockout by restarting.

⚠ **`SocialBridge.GuildOf` has the same problem in a third shape.** `GuildId.None` for somebody whose
guild was never loaded reads as *"in no guild"*, which admits them to a rival's guild chat and drops
the tag their hall's permissions hang off. Same treatment: `IsWarm`, `ColdReads`, `Cold`.

## The roster this realm holds is only the members it can name

⚠ **A 500-member guild has maybe thirty of them online, and the rest cannot be seated at all.** A
gameplay `PlayerId` is a session id widened, so a member who is not connected *to this realm* has no
gameplay id. That is the same partial view `LedgerBridge` keeps of a balance and is not a defect —
but it makes one thing lethal: **a partial roster must never be written back as the whole truth**, or
the write deletes everybody who was offline.

⚠ **So a guild is written as operations, and `SaveGuild` is counted rather than obeyed.** Two things
are missing from a state-shaped save and neither can be recovered: the roster is partial, and a diff
of two rosters cannot say *who did it* — which every `IGuildGrain` method needs, because every guild
rule is about authority. `Invite`, `Kick`, `Promote` and `Rename` are the same operations with the
actor kept, and a non-zero `StateWrites` says something in the game is still going the other way.

⚠ **A graph is state-shaped and safely so**, and the difference is the point: it has one owner, every
change in it is theirs, and nothing has been thrown away by handing over the whole thing. Only the
nameable part is diffed; a tie to somebody this realm cannot name is carried through untouched.

⚠ **A block on somebody offline leaks unless it is re-seated when they arrive.** The person blocked is
usually not here, so the block is a `PlayerKey` in the durable set and nothing in any graph —
`SocialGraphs.IsSevered` answers false and they can whisper, invite and trade. `Admitted` is the
sweep, and a realm that forgets to call it has a block that does not work.

⚠ **`SocialBridge.Divergences` is not expected to be zero**, unlike `LedgerBridge`'s. A cap of 500
measured against thirty seated members will say yes when the guild is full, and the grain will refuse
it. That is the trade this bridge makes on purpose; what the counter is for is noticing when the
number stops looking like the online fraction of a roster.

⚠ **A refusal does not roll the view back.** Undoing a join two frames later is a player who was in
the guild, saw the roster, said hello and was silently ejected — and the next `Warmed` corrects it
from the authority anyway.

## Every codec loads through a seat and never through the rules

Each of the five goes in through a *seating* method on its gameplay object, and every one of those
seams exists because replaying the rules does something specific and bad on login.

| Codec | What the rules would do instead |
|---|---|
| `ProgressionSection` | `SetLevel` zeroes the experience towards the next level — every login. |
| `ProgressionSection` | `Allocate` re-validates a build, so a moved prerequisite wipes it with no refund and no message. |
| `ProgressionSection` | `Train` clamps to today's cap, destroying the difference permanently; the next `Train` clamps late enough to be reversible. |
| `QuestSection` | `Accept` asks the requirements, so somebody who took a quest at level ten is asked again at level nine. |
| `QuestSection` | Replaying the advances announces every objective again and fires a reward chain twice. |
| `ExplorationSection` | `Discover(map, point, null)` skips the requirements and *still* raises `Found` — a toast per landmark, plus the map-complete fanfare. |
| `WardrobeSection` | `Show` refuses an appearance since taken back, throwing the player's choice away for good. |

⚠ **Nothing build-scoped goes in the bytes, and there are two ways to get that wrong.** A
`GameplayTag` is an index into a pre-order walk of the tag tree — *adding one tag renumbers every tag
after it* — so `WardrobeSection` writes slot **names**. A gameplay `PlayerId` is a session id
widened, so `ProfilePityStore` writes only the loot table; a profile already names the character, and
keeping the id meant every lookup missing after a transfer with the rows still in the profile. A
`DefId` is safe in both places because it hashes an address rather than indexing a table.

⚠ **What this build does not understand is preserved entry by entry, not just section by section.** A
profession or a tree with no definition here is carried through, and a quest's *history* survives the
quest itself being gone — history is what `QuestRepeat.Once` reads, and an id is all it needs. The
one exception is an *active* quest with no template: no stages, no objectives, no tags, nothing to
hold. The bytes stay in the profile and it comes back on a build that knows it.

⚠ **A resized map loses its fog, once, and `ExplorationSection.Resized` is how anybody finds out.** A
bitmap read into a grid of a different width is not visibly wrong — it is an explored map that has
quietly become diagonal stripes.

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

⚠ **There is no `Restore(…, 0)` that undoes a seed**, and a realm that thinks there is keeps every
departed player's purse — `Restore` refuses a non-positive amount, so the line reads as the mirror of
admission and does nothing at all. The mirror is `MemoryEconomyLedger.Release`.

### The outbox is asked before the projection, and that order is a guard

A realm has two records of what it has already done. The projection's key set is bounded by a
`KeyHorizon`; the outbox is exact for everything started here and not yet confirmed. **The exact one
is asked first**, which shrinks what the horizon has to cover from *every retry there will ever be* to
*every retry after the write is durable* — and inside that window a horizon set too short cannot
double anything, whatever it says.

⚠ **Asking the projection first would mean the movements had already been applied a second time by the
time anything noticed.** The database would still refuse the duplicate write, because that is what its
own key is for; what it cannot fix is that the realm's balances would already have moved twice, on a
realm whose database is right. `Deduplicated` counts it.

### A departing player is unseated from every graph, not just their own

`Forget` drops their graph *and* sweeps the durable set for anybody still here who held a tie to them
— the exact mirror of `Admitted`. A gameplay id is never issued twice, so a tie left pointing at a
departed one is never replaced: they come back as a different number and are seated beside the old
one. ⚠ **The order in a realm's release path matters**: `Forget` before the identity map lets them go,
or the sweep has nothing to look up.

## See also

- [`Vixen.Live.Persistence`](../Vixen.Live.Persistence/README.md) — the ledger and the repositories.
- [`Vixen.Gameplay.Economy`](../../Gameplay/Vixen.Gameplay.Economy/README.md) — the other side.
