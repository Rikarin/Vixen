---
title: The gameplay bridge
slug: live/gameplay-bridge
kind: guide
area: Live
summary: Where doc 28's rules meet doc 27's storage — the third player identity, and four views that answer in the frame and write down afterwards without ever awaiting a grain.
api: [T:Vixen.Live.Gameplay.IGameplayIdentity, T:Vixen.Live.Gameplay.GameplayIdentityMap, T:Vixen.Live.Gameplay.LedgerBridge, T:Vixen.Live.Gameplay.PendingWrite, T:Vixen.Live.Gameplay.BridgeRefusal, T:Vixen.Live.Gameplay.LockoutBridge, T:Vixen.Live.Gameplay.PendingLockout, T:Vixen.Live.Gameplay.SocialBridge, T:Vixen.Live.Gameplay.GuildEdit, T:Vixen.Live.Gameplay.GuildEditKind, T:Vixen.Live.Gameplay.PendingGraph, T:Vixen.Live.Gameplay.SocialLink, T:Vixen.Live.Gameplay.PlayerProfile, T:Vixen.Live.Gameplay.ProfileSectionId, T:Vixen.Live.Gameplay.ProfileSections, T:Vixen.Live.Gameplay.IProfileSection, T:Vixen.Live.Gameplay.ProfileBinder, T:Vixen.Live.Gameplay.ProfileFormatException, T:Vixen.Live.Gameplay.CheckpointPolicy, T:Vixen.Live.Gameplay.CheckpointReason, T:Vixen.Live.Gameplay.ProfilePityStore, T:Vixen.Live.Gameplay.ProgressionSection, T:Vixen.Live.Gameplay.QuestSection, T:Vixen.Live.Gameplay.ExplorationSection, T:Vixen.Live.Gameplay.WardrobeSection]
tags: [live, mmo, gameplay, persistence, ledger, guild, lockout, profile]
since: 0.1
status: preview
related: [live/durable-state, live/writing-a-realm, gameplay/economy, gameplay/social, gameplay/instances]
---

## What it is

`Vixen.Live.Gameplay` is the only assembly that references both halves of the engine's server story:
the gameplay libraries of doc 28 and the persistence and cluster of doc 27. It exists because the
layer rules make it the only place that can — `Live/` may reference `Gameplay/`, nothing in
`Gameplay/` may reference `Live/`, so the join has nowhere else to live.

It is five things:

| | |
|---|---|
| `IGameplayIdentity` · `GameplayIdentityMap` | The third player identity, joined to the other two. |
| `LedgerBridge` | Doc 28's `IEconomyLedger` answered in the frame, written through doc 27's journal. |
| `LockoutBridge` | Doc 28's `ILockoutStore` over the fleet-wide `IInstanceGrain`. |
| `SocialBridge` | Doc 28's `ISocialStore` over `IGuildGrain`, and friends and blocks. |
| `PlayerProfile` · `CheckpointPolicy` | `PlayerRecord.Profile` given a shape, and when it is written. |
| `ProgressionSection` · `QuestSection` · `ExplorationSection` · `WardrobeSection` · `ProfilePityStore` | The five codecs that fill it. |

## What it is for

### Three player identities, and only two were joined

There are three, and the engine had joined the first two:

- `Vixen.Net.Sessions.PlayerId` — a `uint` the session assigns.
- `Vixen.Live.PlayerKey` — two `Guid`s the database is keyed by. `RealmPlayer` already joined these.
- `Vixen.Gameplay.PlayerId` — a `ulong` every rule in doc 28 passes around, and **the one every
  durable write starts from**.

`GameplayIdentityMap` is that join, and it is a **widening rather than a hash**: a gameplay id *is*
its session id in a wider integer, populated as a table at admission. Hashing a 256-bit `PlayerKey`
into 64 bits collides, and two players who collided would write each other's inventory.

⚠ **A gameplay `PlayerId` is realm-scoped and must never reach the database.** Doc 28 calls one
*"stable for as long as a party invite or a mute list has to be"*, and those are not the same length:
a party lasts a session, a mute list outlives every realm the player will ever be on. A saved row
carrying a raw gameplay id means somebody else on the next realm.

### A view in the frame, an outbox behind it

The three bridges have one shape, and ADR-016 is why: *"Orleans is asked, not awaited."* Every
interface here is **synchronous**, because a purchase, a zone-in and a guild-chat line all ask it
mid-frame, and the thing that owns the true answer is a round trip. So each bridge answers from an
in-memory view and posts the change to an outbox the realm drains off the frame path.

⚠ **A drained write is not a removed write.** `Drain()` returns what is waiting and leaves it there;
`Settle` is what takes it out. In flight is not done, and losing a ledger intent is losing an item.

### Assets go in the ledger, counters go in the profile

That one sentence decides the storage of everything a character owns, and it decides it from a
property of the data rather than from taste. **An asset can be duplicated**, so every movement of one
is a journal row with an idempotency key. **A counter cannot** — writing level 42 twice leaves you at
42 — so a counter is written on a cadence, and a crash loses at most one interval.

⚠ That loss is the correct trade and not a compromise: making a counter durable per kill puts a
database round trip on the combat path. It is also why `ProfilePityStore` can be synchronous where
`LedgerBridge` could not.

### What each bridge is afraid of

Each one is arranged around a different way of being wrong, and they are worth reading as three
answers to the same question — *what does a partial view get wrong, and how loud is it?*

- **`LedgerBridge` — the balance that disagrees.** The projection is authoritative for the frame and
  the database is the audit trail, which is not optimism: the lease already says exactly one realm
  writes. So `Insufficient` coming back from the database is a **defect and not a refusal** — the
  projection checked first, and disagreement means the single-writer property broke. It is counted in
  `Divergences`, raised, and kept as evidence. And `Supersede()` is not an undo: ADR-021 buffers and
  re-flushes, so every waiting write is *restamped* when the lease returns.

- **`LockoutBridge` — the absence that reads as permission.** An unknown balance reads as zero and
  refuses a purchase, which is annoying and safe. An unknown *lockout* reads as `null`, and
  `ILockoutStore.Find` defines `null` as **"not locked"** — so a player whose lockouts were never
  loaded is admitted to a raid they are already saved to, and the run they get is one the fleet
  cannot take back. That is doc 28's *"a lockout one shard knew about is a lockout a player evades by
  zoning"* reopened from inside. The interface cannot say *"I do not know"*, so `IsWarm` is what
  admission checks and a cold `Find` increments `ColdReads` and raises `Cold`.

- **`SocialBridge` — the roster that is only partly here.** A 500-member guild has maybe thirty
  members online, and a member who is not connected *to this realm* has no gameplay id at all, so
  they cannot be seated. That is the same partial view the other two keep — but it makes one thing
  lethal: **a partial roster must never be written back as the whole truth**, or the write deletes
  everybody who was offline.

### Why a guild is written as operations and a graph is written as state

`SocialBridge.SaveGuild` is implemented, counted in `StateWrites`, and **writes nothing**. Two things
are missing from a state-shaped save of a guild and neither can be recovered afterwards:

1. The roster is partial, so writing it down deletes the absent.
2. A diff of two rosters cannot say **who did it** — and every `IGuildGrain` method needs an actor,
   because every guild rule is about authority.

`Invite`, `Kick`, `Promote` and `Rename` are the same operations with the actor kept, and each queues
a `GuildEdit`. A member this realm cannot name is in neither side of any operation, so they are never
touched — which is exactly the property that makes a partial roster safe.

A **graph** is state-shaped and safely so, and the difference is worth naming: it has one owner and
every change in it is theirs, so handing over the whole thing throws nothing away. Only the nameable
part is diffed; a tie to somebody this realm cannot name is carried through untouched.

⚠ **A block on somebody offline is the case to get right.** The person blocked is usually not here,
so the block lives in the durable set and in no graph — and `SocialGraphs.IsSevered` answers `false`,
which means they can whisper, invite and trade the moment they log in. `SocialBridge.Admitted` is the
sweep that seats them, and a realm that forgets to call it has a block that leaks.

### An unknown profile section is preserved, never dropped

`PlayerRecord.Profile` is opaque to the persistence layer on purpose — its schema is the game's.
`PlayerProfile` gives that blob a shape without giving persistence one: a map of `ProfileSectionId`
to bytes, written in id order.

⚠ **The container knows nothing about types, and that is the whole point.** Doc 27 § Upgrades
fragments a population by version deliberately, so during a rollout an old realm and a new realm both
write the same character. If the old one dropped the section the new one had added, a player who
zoned the wrong way would lose it — silently, and only some of the time. A map of id to bytes cannot
make that mistake, and the codecs that know types sit above it as `IProfileSection`.

Two further rules fall out: **sections are written in id order**, so two realms holding the same state
produce the same bytes; and **writing the same bytes back is not a change**, or every checkpoint looks
like one and the row is rewritten on a cadence for ever.

## Using it

A realm builds one identity map and one of each bridge it needs, fills them at admission, answers
from them during the frame, and drains them afterwards.

```csharp no-compile="a realm's admission path, which is Vixen.Live.Realm's"
var identity = new GameplayIdentityMap();
var lockouts = new LockoutBridge(identity);
var social = new SocialBridge(identity, library);

// Admission: the join, then everything durable this player needs answered in a frame.
var player = identity.Admit(key, session);

lockouts.Warmed(player, await grains.Instances.LockoutsOf(key));
social.Warmed(player, await persistence.Guilds.ReadAsync(guildId, cancellation));
social.Warmed(key, storedLinks);
social.Admitted(key, player);
```

⚠ **Warming with an empty list is meaningful.** "Saved to nothing" and "nobody has asked" are the
same absence in a cache and must not be the same fact — which is why `LockoutBridge.Warmed` takes an
empty sequence and `SocialBridge.Warmed` takes a `null` row, and both mark the player warm.

Draining is the realm's own job, off the frame path, and settling is what removes a write:

```csharp no-compile="the realm's off-frame drain"
foreach (var edit in social.Drain()) {
    var outcome = await grains.Guild(edit.Guild).Apply(edit);

    social.Settle(edit, outcome.Refusal);
}
```

⚠ **A refusal does not roll the view back.** Undoing a join two frames later is a player who was in
the guild, saw the roster, said hello and was silently ejected — and the next `Warmed` corrects it
from the authority anyway. What the realm owes is telling them, which is what `Refused` is for.

### Registering a profile section

`IProfileSection` is the seam that keeps this assembly from being a bundle. Doc 28's whole shape is
that every library is declinable, so a game that took quests and declined exploration should not
carry an exploration codec. A section registers itself, and whatever is never registered is still
preserved.

```csharp no-compile="what a game's composition root does once"
var binder = new ProfileBinder()
    .Add(new ProgressionSection(progression, checkpoint))
    .Add(new QuestSection(journal, checkpoint))
    .Add(new ExplorationSection(exploration, checkpoint))
    .Add(new WardrobeSection(wardrobe, catalog.Tags, checkpoint))
    .Add(new ProfilePityStore(checkpoint));

binder.Load(PlayerProfile.Read(record.Profile));
```

⚠ **Two sections claiming one id are refused rather than last-wins.** Section names are hashed, so a
collision is possible; one section silently reading the other's bytes presents as a character whose
quests are full of somebody else's fog.

Five are shipped: `ProgressionSection`, `QuestSection`, `ExplorationSection`, `WardrobeSection` and
`ProfilePityStore`. Each one loads through a **seating** method on its gameplay object rather than
through the rules that made the state, and every one of those seams was added for a failure the
rules would otherwise cause on login:

| Codec | What replaying the rules would do |
|---|---|
| `ProgressionSection` | `SetLevel` zeroes the experience towards the next level — **every login**. |
| `ProgressionSection` | `Allocate` re-validates a talent build, so a patch that moved a prerequisite wipes it with no refund and no message. |
| `ProgressionSection` | `Train` clamps to today's cap, so a patch that lowers one destroys the difference permanently — where the next `Train` clamps late enough to be reversible. |
| `QuestSection` | `Accept` asks the requirements, so a character who took a quest at level ten is asked again at level nine. |
| `QuestSection` | Replaying the objective advances announces every objective again and fires a reward chain twice. |
| `ExplorationSection` | `Discover` with a null context skips the requirements and *still* raises `Found` — a toast for every landmark ever visited, plus the map-complete fanfare. |
| `WardrobeSection` | `Show` refuses an appearance that has since been taken back, throwing the player's choice away for good. |

Two rules about **what a codec may write down** are worth reading together, because they are the
same rule:

⚠ **Nothing build-scoped goes in the bytes.** A `GameplayTag` is an index into a pre-order walk of
the tag tree, so *adding one tag renumbers every tag after it* — `WardrobeSection` therefore writes
slot **names**. A gameplay `PlayerId` is a session id widened, so `ProfilePityStore` writes only the
loot table: a profile already names the character, and keeping the id would mean every lookup missing
after a transfer with the rows still sitting in the profile. A `DefId` is safe in both places,
because it is a hash of an address rather than a position in a table.

⚠ **What this build does not understand is preserved, section by section and entry by entry.** A
profession or a talent tree with no definition here is carried through; a quest's *history* is kept
for a quest this build has lost, because history is what `QuestRepeat.Once` reads and an id is all it
needs. The one deliberate exception is an *active* quest with no template: it has no stages, no
objectives and no tags, so there is nothing to hold — the bytes stay in the profile and it comes back
on a build that knows it.

### The checkpoint

`CheckpointPolicy` decides when the counters are written down.

⚠ **A failed write leaves it dirty *and* does not restart the clock.** Clearing the flag loses the
interval for good; restarting the clock means a store that is briefly unhappy is retried a whole
cadence later, which turns a five-second outage into five minutes of lost progress.

⚠ **Transfer and logout write only when there is something to write.** "Always on transfer" reads as
unconditional and should not be: a character nobody changed has the same bytes stored, and a round
trip to write them is a round trip inside the overlap window a transfer spends loading a map.

## Examples

A tick, in the order the pieces are meant to be used — ask, act, mark dirty, and let the drain
happen somewhere else:

```csharp no-compile="a realm tick, which is Vixen.Live.Realm's"
// Zone-in. IsWarm first, because Find cannot say "I do not know".
if (!lockouts.IsWarm(player)) {
    return Refuse(player, "still loading");
}

if (lockouts.Find(player, instance, "heroic") is { } saved && saved.Completions > 0) {
    return Refuse(player, "already saved");
}

// A boss dies. The lockout is recorded here and written down later.
lockouts.Save(new(player, instance, "heroic", reset, completions: 1));

// Experience is a counter, so it moves in memory and the checkpoint decides when it lands.
progression.Award(player, experience);
checkpoint.Touch();

if (checkpoint.Due(now, out var reason)) {
    Enqueue(new CheckpointRequest(player, reason));
}
```

Reading a lockout for somebody who was never warmed is the mistake the type is built to make loud,
so wire the counter to something that notices:

```csharp no-compile="the diagnostics a fleet should not be run without"
lockouts.Cold += who => log.Error("Lockouts read cold for {Player} — they may have been let into a saved raid.", who);
ledger.Diverged += divergence => log.Error("The ledger disagreed with the projection: {Divergence}", divergence);
social.Cold += who => log.Warning("Guild read cold for {Player} — they will look unguilded.", who);
```

⚠ `LedgerBridge.Divergences` should be **zero** in normal running; a non-zero value means the
single-writer property broke. `SocialBridge.Divergences` should **not** be zero — a cap measured
against a partial roster is expected to be wrong sometimes, and the trade is deliberate. What that
counter is for is noticing when the number stops looking like the online fraction of a roster.

## See also

- [Durable state and the ledger](durable-state.md) — the journal, the repositories and the fence.
- [Writing a realm](writing-a-realm.md) — where the drain belongs in a tick.
- [Economy](../gameplay/economy.md) — `IEconomyLedger`, from the other side.
- [Social](../gameplay/social.md) — `ISocialStore`, `Guild.Seat` and `SocialGraph.Seat`.
- [Instances](../gameplay/instances.md) — `ILockoutStore` and what a lockout is.
