---
title: Parties, guilds, friends and presence
slug: gameplay/social
kind: guide
area: Gameplay
summary: Three kinds of group and one implementation, a guild whose permission matrix is a tag query per action, and a block that is one-way as a fact and two-way as a rule.
api: [T:Vixen.Gameplay.PlayerId, T:Vixen.Gameplay.Social.GroupId, T:Vixen.Gameplay.Social.GroupKind, T:Vixen.Gameplay.Social.GroupRefusal, T:Vixen.Gameplay.Social.GroupPolicyDefinition, T:Vixen.Gameplay.Social.GroupPolicy, T:Vixen.Gameplay.Social.GroupMember, T:Vixen.Gameplay.Social.GroupInvite, T:Vixen.Gameplay.Social.PlayerGroup, T:Vixen.Gameplay.Social.GuildId, T:Vixen.Gameplay.Social.GuildRefusal, T:Vixen.Gameplay.Social.GuildRankDefinition, T:Vixen.Gameplay.Social.GuildCharterDefinition, T:Vixen.Gameplay.Social.GuildCharter, T:Vixen.Gameplay.Social.GuildRank, T:Vixen.Gameplay.Social.Guild, T:Vixen.Gameplay.Social.SocialTie, T:Vixen.Gameplay.Social.SocialGraph, T:Vixen.Gameplay.Social.SocialGraphs, T:Vixen.Gameplay.Social.PresenceStatus, T:Vixen.Gameplay.Social.PresenceRecord, T:Vixen.Gameplay.Social.PresenceBook, T:Vixen.Gameplay.Social.SocialLibrary, T:Vixen.Gameplay.Social.ISocialStore, T:Vixen.Gameplay.Social.MemorySocialStore, T:Vixen.Gameplay.Social.SocialModule]
tags: [gameplay, social, party, squad, guild, friends, presence, mmo]
since: 0.1
status: preview
related: [gameplay/chat, gameplay/tags, gameplay/requirements]
---

## What it is

A **`PlayerGroup`** is a party, a squad or a team — one type with a **`GroupPolicy`** that says how
many fit, whether there are subgroups, who may invite and whether anybody may leave. A **`Guild`** is a
roster over a rank ladder whose permissions are tag queries. A **`SocialGraph`** is one player's
friends and blocks; a **`PresenceBook`** is who is online, redacted per viewer.

## What it is for

Every grouping a game has, and the two questions every one of them ends up asking: *who is in this*
and *may they do that*.

## Using it

Compile a catalog into a `SocialLibrary` for the policies and charters, then make groups and guilds
against them. Everything returns a refusal rather than throwing, because every one of these operations
arrives from a client.

⚠ **A non-empty group has exactly one leader, always**, and leadership passes to the longest-standing
remaining member. A leaderless group cannot invite, kick or disband — it is one an operator has to
clean up — and "whoever is first in the list" is not a rule a client can predict.

⚠ **Capacity counts the standing invites.** A party of four with three invites out becomes a party of
seven the moment they all accept.

⚠ **A guild always has exactly one member at rank zero.** Anything that would remove the last one is
`WouldStrand`; the leader hands it over first, which `SetRank(…, 0)` does in one operation.

⚠ **The top rank carries every permission whatever the charter said** — a charter that forgot to give
the leader `Guild.Permission.Invite` would otherwise brick every guild founded on it.

⚠ **A block is one-way as a fact and two-way as a rule.** Only one of them chose it, but the blocked
player must not still be able to whisper, invite or trade.

⚠ **Presence is redacted in the book rather than at the UI.** An invisible player whose map went over
the wire is one a packet capture can follow.

### Reading one back

`Guild.Seat`, `Guild.Unseat` and `SocialGraph.Seat` are the unchecked door state comes in through, and
they are not player actions. `HousePlot.Assign` is the same seam for the same reason: `Add` asks a
permission and `SetRank` asks who outranks whom, and a roster arriving from storage has nobody asking
either. Replaying the checked calls instead would re-derive yesterday's state against today's content
and quietly drop the standing a patch has since made illegal.

Three rules come with them, and each is a mistake the alternative makes:

⚠ **A rank past the bottom rung lands on the bottom rather than being refused.** A charter edited to
remove a rung leaves every member who stood on it holding a rank the ladder no longer has, and
refusing them would *delete those members* the next time the guild was read. Landing them at the
bottom loses a rank; refusing them loses the player.

⚠ **Seating does not keep the one-leader invariant**, which nothing else in `Guild` breaks. Two at
rank zero makes `Leader` answer with whichever the roster yields first. The caller is the authority
precisely because it is the one that knows which is true, so it is the one that has to check.

⚠ **A `SocialTie` is one value and not a set of flags, and seating one clears the other three.**
`Block` drops the friendship and both requests, so "blocked friend" is not a state this graph can be
in — and a graph read back with somebody on two lists is one where the block's guarantee has already
failed.

`SocialGraph.Ties()` is the way back out, in player order so that two realms holding the same graph
write the same bytes.

## Examples

A raid squad:

```yaml
# Assets/Social/squad.vxdef
!GroupPolicyDefinition
kind: Squad
displayName: Squad
maximumMembers: 50
subgroupSize: 5
membersMayInvite: true
tag: Group.Squad
roles: [ Role.Tank, Role.Healer, Role.Damage ]
```

Running one:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Social;

static class Squads {
    public static GroupRefusal Ask(PlayerGroup squad, PlayerId leader, PlayerId recruit, float now, SocialGraphs graphs) =>
        // The graph is passed so that a block refuses the invite rather than the accept.
        squad.Invite(leader, recruit, now, graphs.Of(leader));

    public static PlayerId WhoLeadsAfter(PlayerGroup squad, PlayerId leaving) {
        squad.Leave(leaving);

        // The longest-standing remaining member, which is what a client predicted too.
        return squad.Leader;
    }
}
```

Asking whether somebody may do something:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Social;

static class Bank {
    public static bool MayWithdraw(Guild guild, PlayerId player, GameplayTagTable tags) =>
        guild.Can(player, tags.RangeOf(SocialModule.Withdraw));
}
```

What a viewer is told:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Social;

static class Friends {
    public static PresenceRecord Show(PresenceBook book, PlayerId viewer, PlayerId subject) =>
        // Invisible, or either of them has blocked the other, and this is an offline record with no
        // map on it — redacted here rather than at whatever draws the list.
        book.As(viewer, subject);
}
```

## See also

- [Chat](gameplay/chat) — which may not reference this, and why that is right.
- [Gameplay tags](gameplay/tags) — what a guild permission is.
- [Requirements](gameplay/requirements) — the same algebra, one layer up.
