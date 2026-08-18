# Vixen.Gameplay.Social

Parties, squads and teams as one group with a policy; guilds with a rank ladder whose permissions are
tag queries; friends, blocks and presence.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Social, the first half of **G4**.

## State

**Built: groups with invites, subgroups, roles and leadership succession; guilds with a permission
matrix and the rules that stop one being bricked; friends, blocks and presence with its redaction;
the unchecked seams a stored roster and a stored graph come back in through. 54 tests.** Chat is
G4's other half.

| | |
|---|---|
| `GroupId` · `GroupKind` · `GroupPolicyDefinition` · `GroupPolicy` | Three kinds, one implementation. |
| `PlayerGroup` · `GroupMember` · `GroupInvite` · `GroupRefusal` | The membership and everything that changes it. |
| `GuildId` · `GuildCharterDefinition` · `GuildCharter` · `GuildRankDefinition` · `GuildRank` | What a *new* guild starts as. |
| `Guild` · `GuildRefusal` | The roster, the ladder, and `Can(player, permission)`. |
| `SocialGraph` · `SocialGraphs` · `SocialTie` | Friends both ways, blocks one way. |
| `PresenceStatus` · `PresenceRecord` · `PresenceBook` | Who is online, redacted per viewer. |
| `SocialLibrary` · `ISocialStore` · `MemorySocialStore` | Compiled content, and the durable seam. |
| `SocialModule` | Two definition types, five permission tags. |

## `PlayerId` went into the kernel, and this library is why

Doc 28's spine allows `Items` and `Combat` to be depended on and nothing else, so **`Chat` cannot
reference `Social`** — which is correct: a game with no parties still has chat. But chat has to name a
sender and this has to name a member, and a type both need with no edge between them can only live
below both. The kernel's event bus was already carrying the same number as a bare `ulong`, so it is
now `PlayerId` there too.

## The five things worth knowing before reading the code

### A party, a squad and a team are one type with a policy

They differ in how many fit, whether there are subgroups, who may invite and whether anybody may
leave — four numbers. Writing three types would be three implementations of *"the leader left, who is
leader now"*, and that is a question games get wrong once per type.

### A non-empty group has exactly one leader, always

A leaderless group cannot invite, cannot kick and cannot be disbanded: it is a group an operator has
to clean up. So leadership passes when a leader leaves or is removed, and it passes to the
**longest-standing remaining member** — not to whoever a list happens to hold first, because the rule
has to be one a client can predict and two servers agree on.

⚠ **A leader kicking themselves is a leave.** The gesture exists in every client and refusing it
leaves a leader who cannot get out of their own party.

⚠ **The group oracle's first run tested nothing, and that is what the applied/refused floors are
for.** Uniform actors spent almost all of their time on an empty group refusing everything: every
invariant held, every assertion passed, and nothing was exercised. A randomised oracle must assert
that a useful fraction of its operations both *applied* and were *refused*, or a change that made
everything fail passes it quietly. The same floors are in
[`Vixen.Gameplay.Inventory`](../Vixen.Gameplay.Inventory/README.md)'s conservation oracle for the
same reason.

### Capacity counts the standing invites

A party of four with three invites out is a party of seven the moment they all accept, and a check
that happens on accept is a check that happens after somebody was told yes. Expired invites stop
counting, which is what `Expire` is for.

### A guild always has exactly one member at rank zero

Every operation that could remove the last one is refused with `WouldStrand`. A guild whose leader
left or demoted themselves cannot invite, cannot promote and cannot be disbanded — and fixing it needs
a permission too. Handing it over first is one extra click and no support tickets.

⚠ **`SetRank(…, 0)` is the handover, in one operation**, because doing it as a promote and a demote
leaves a window with two leaders or none.

⚠ **Nobody may act on somebody at or above their own rank**, and promotion is checked against *both*
where the target is and where they are going: an officer who could promote somebody to their own rank
has promoted somebody who can now demote them.

⚠ **The top rank carries every permission whatever the charter said.** This is the one hard-coded rule
in the matrix, and it exists because a charter authored without `Guild.Permission.Invite` on the
leader rung would brick every guild founded on it. The test content authors it that way on purpose.

### A charter is content and a guild is not

A guild is made by players at runtime, so it is durable state; what a designer authors is what a *new*
one starts with. Getting this backwards makes every guild in the game share one editable object.

### Friendship is mutual and asked for; a block is one-way and silent

Two different relations. ⚠ **Blocking unfriends and drops every pending request both ways**, or the
person just blocked is still on the list and still visible in presence. ⚠ **A block is one-way as a
fact and two-way as a rule** — only one of them chose it, but the blocked player must not still be
able to whisper, invite or trade, which is every avenue the block was for.

⚠ **Presence is redacted in the book, not at the UI.** An invisible player whose map still went over
the wire is one anybody with a packet capture can follow. A player always sees themselves.

### State comes back in through one unchecked door

`Guild.Seat`, `Guild.Unseat` and `SocialGraph.Seat` are not player actions, and `HousePlot.Assign` is
the precedent. `Add` asks a permission, `SetRank` asks who outranks whom and `Request` refuses
somebody who is blocked — those are the rules for *making* a tie, and a roster arriving from storage
has nobody asking any of them. Replaying the checked calls would re-derive yesterday's state against
today's content and quietly drop whatever a patch has since made illegal.

⚠ **A rank past the bottom rung lands on the bottom rather than being refused.** A charter edited to
remove a rung leaves members holding a rank the ladder no longer has; refusing them would *delete
those members* the next time the guild was read. Landing them at the bottom loses a rank, and
refusing them loses the player.

⚠ **Seating does not keep the one-leader invariant**, which nothing else here breaks — the caller is
the authority precisely because it is the one that knows which of two rank-zero members is real.

⚠ **`SocialTie` is one value rather than a set of flags, and seating one clears the other three.**
`Block` drops the friendship and both requests, so "blocked friend" is not a state this graph can be
in — and a flags enum would invite storage to write one.

### A graph is made on demand and somebody has to take it away

`SocialGraphs.Of` mints one for any id that does not have one, and nothing ever removed one — so a
realm held a graph for every player who had ever been on it. `Samples/14-Mmo`'s soak, where map travel
means admitting and releasing five hundred players an hour, measured **130 MB of them over thirty
minutes**. `Forget` is the missing half.

⚠ **`HasBlocked` deliberately does not mint one.** A rule consults it for every whisper, invite and
trade, so a question that created a permanent graph for both parties would make *asking* the thing
that leaks.

⚠ **Dropping the departed player's own graph is only half the job, and the other half cannot live
here.** A gameplay id is never issued twice, so anybody still online who had a tie to them keeps an id
no re-admission will replace — they come back as a different number and are seated beside their own
ghost. Only the durable set knows who held a tie to whom, and a graph is keyed by a gameplay id, so
the reverse sweep belongs to `Vixen.Live.Gameplay.SocialBridge.Forget` and is the exact mirror of its
admission sweep.

## What is owed

- ~~**Durability.**~~ Built. A roster and a friends list are a grain's — doc 27 hands `IGuildGrain`
  to doc 28 rather than building it. `ISocialStore` is the seam, `MemorySocialStore` is the test's,
  and `Vixen.Live.Gameplay.SocialBridge` is the real one.
- **The guild bank.** A bank tab is a container with a permission on it, which needs
  `Vixen.Gameplay.Inventory` — and this library takes no dependency on it, so the bank belongs
  wherever the two are already both present, which is G5.
- **Placement.** Doc 27 § Placement scores a party member at 10 000 and a guild member at 400, and
  reads those ids off the map rather than asking here. Nothing is owed *to* placement; this is a note
  that the coupling deliberately does not exist.
- **Cross-realm groups.** A party spanning a transfer is doc 27's, and what this owes it is only that
  the membership is a value a grain can hold — which it is.
