# Vixen.Gameplay.Social

Parties, squads and teams as one group with a policy; guilds with a rank ladder whose permissions are
tag queries; friends, blocks and presence.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Social, the first half of **G4**.

## State

**Built: groups with invites, subgroups, roles and leadership succession; guilds with a permission
matrix and the rules that stop one being bricked; friends, blocks and presence with its redaction.
43 tests.** Chat is G4's other half.

| | |
|---|---|
| `GroupId` · `GroupKind` · `GroupPolicyDefinition` · `GroupPolicy` | Three kinds, one implementation. |
| `PlayerGroup` · `GroupMember` · `GroupInvite` · `GroupRefusal` | The membership and everything that changes it. |
| `GuildId` · `GuildCharterDefinition` · `GuildCharter` · `GuildRankDefinition` · `GuildRank` | What a *new* guild starts as. |
| `Guild` · `GuildRefusal` | The roster, the ladder, and `Can(player, permission)`. |
| `SocialGraph` · `SocialGraphs` | Friends both ways, blocks one way. |
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

## What is owed

- **Durability.** A roster and a friends list are a grain's — doc 27 hands `IGuildGrain` to doc 28
  rather than building it. `ISocialStore` is the seam and `MemorySocialStore` is the test's; the real
  one is task **#27**'s.
- **The guild bank.** A bank tab is a container with a permission on it, which needs
  `Vixen.Gameplay.Inventory` — and this library takes no dependency on it, so the bank belongs
  wherever the two are already both present, which is G5.
- **Placement.** Doc 27 § Placement scores a party member at 10 000 and a guild member at 400, and
  reads those ids off the map rather than asking here. Nothing is owed *to* placement; this is a note
  that the coupling deliberately does not exist.
- **Cross-realm groups.** A party spanning a transfer is doc 27's, and what this owes it is only that
  the membership is a value a grain can hold — which it is.
