// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Gameplay;
using Vixen.Gameplay.Social;
using Vixen.Live.Persistence;
using Xunit;
using NetworkPlayerId = Vixen.Net.Sessions.PlayerId;

namespace Vixen.Live.Gameplay.Tests;

public class SocialBridgeTests {
    static readonly Guid TheVigil = Guid.NewGuid();

    readonly GameplayIdentityMap identity = new();
    readonly SocialBridge bridge;

    readonly PlayerKey ana = new(Guid.NewGuid(), Guid.NewGuid());
    readonly PlayerKey bo = new(Guid.NewGuid(), Guid.NewGuid());
    readonly PlayerKey cass = new(Guid.NewGuid(), Guid.NewGuid());

    PlayerId Ana { get; }

    PlayerId Bo { get; }

    public SocialBridgeTests() {
        bridge = new(identity);
        Ana = identity.Admit(ana, new NetworkPlayerId(1));
        Bo = identity.Admit(bo, new NetworkPlayerId(2));
    }

    // Cass is deliberately never admitted: she is the offline member every test here is about.
    static GuildRow Row(params (PlayerKey Player, int Rank)[] members) =>
        new(
            TheVigil,
            "guilds/standard",
            "The Vigil",
            [.. members.Select(member => new GuildMemberRow(member.Player, member.Rank, default))],
            ImmutableDictionary<int, string>.Empty,
            default,
            1
        );

    GuildRow Founded() => Row((ana, 0), (bo, 1), (cass, 1));

    // ── The partial roster ────────────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyTheMembersThisRealmCanNameAreSeated() {
        // ⚠ The finding this type is arranged around. A gameplay PlayerId is a session id widened, so
        // a member who is not connected *to this realm* has no gameplay id and cannot be seated at
        // all — and a 500-member guild has maybe thirty of them online.
        var guild = bridge.Warmed(Ana, Founded());

        Assert.NotNull(guild);
        Assert.Equal(2, guild.Count);
        Assert.Equal(Ana, guild.Leader);
        Assert.Equal(1, guild.RankOf(Bo));
    }

    [Fact]
    public void AnAbsentMemberCannotBeKickedFromHereAtAll() {
        // ⚠ And that is correct rather than a gap: kicking somebody who is offline is a guild-panel
        // action against the grain, which is doc 27's service plane. It never reaches a realm.
        bridge.Warmed(Ana, Founded());

        var cassie = new PlayerId(0xC455);

        Assert.Equal(GuildRefusal.NotIn, bridge.Kick(Ana, new(TheVigil), cassie));
        Assert.Equal(0, bridge.Pending);
    }

    [Fact]
    public void AGuildAlreadyLoadedIsNotRebuiltOverAnUnwrittenEdit() {
        // ⚠ Rebuilding from the row would throw away every edit made since it was loaded and not yet
        // written down, which presents as a kick that comes back.
        bridge.Warmed(Ana, Founded());
        bridge.Kick(Ana, new(TheVigil), Bo);

        bridge.Warmed(Bo, Founded());

        Assert.Equal(1, bridge.Pending);
        Assert.Equal(1, bridge.LoadGuild(new(TheVigil))!.RankOf(Bo));
    }

    // ── The cold-read problem ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AskingWhichGuildSomebodyUnloadedIsInIsCountedAndRaised() {
        // ⚠ LockoutBridge's problem again. GuildId.None reads as "in no guild", which admits them to
        // a rival's guild chat and drops the tag their hall's permissions hang off.
        var raised = 0;

        bridge.Cold += _ => raised++;

        Assert.Equal(GuildId.None, bridge.GuildOf(Ana));
        Assert.Equal(1, bridge.ColdReads);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void SomebodyInNoGuildIsADifferentFactFromAnUnloadedOne() {
        Assert.Null(bridge.Warmed(Ana, null));
        Assert.True(bridge.IsWarm(Ana));
        Assert.Equal(GuildId.None, bridge.GuildOf(Ana));
        Assert.Equal(0, bridge.ColdReads);
    }

    [Fact]
    public void AGraphNobodyLoadedReadsAsNullRatherThanEmpty() {
        Assert.Null(bridge.LoadGraph(Ana));
        Assert.Equal(1, bridge.ColdReads);
    }

    // ── Writing by operation ──────────────────────────────────────────────────────────────────

    [Fact]
    public void AnInviteIsAppliedHereAndQueuedWithWhoDidIt() {
        bridge.Warmed(Ana, Row((ana, 0)));

        Assert.Equal(GuildRefusal.None, bridge.Invite(Ana, new(TheVigil), Bo));
        Assert.Equal(new GuildId(TheVigil), bridge.GuildOf(Bo));

        var edit = Assert.Single(bridge.Drain());

        Assert.Equal(GuildEditKind.Add, edit.Kind);
        Assert.Equal(ana, edit.By);
        Assert.Equal(bo, edit.Player);
    }

    [Fact]
    public void ARefusalHereQueuesNothing() {
        bridge.Warmed(Ana, Founded());

        // Bo is rank 1 and the charter's bottom rung carries no Invite permission.
        Assert.Equal(GuildRefusal.NoPermission, bridge.Invite(Bo, new(TheVigil), new PlayerId(77), new(1, 2)));
        Assert.Equal(0, bridge.Pending);
    }

    [Fact]
    public void APromotionQueuesTheRankAskedForRatherThanTheRankLandedOn() {
        // A handover moves two people, and the grain does the same thing from the same call — so
        // replaying the request is what keeps the two in step.
        bridge.Warmed(Ana, Founded());

        Assert.Equal(GuildRefusal.None, bridge.Promote(Ana, new(TheVigil), Bo, 0));

        var edit = Assert.Single(bridge.Drain());

        Assert.Equal(GuildEditKind.SetRank, edit.Kind);
        Assert.Equal(0, edit.Rank);
        Assert.Equal(Bo, bridge.LoadGuild(new(TheVigil))!.Leader);
    }

    [Fact]
    public void SavingAGuildByStateIsCountedAndWritesNothing() {
        // ⚠ Two things are missing from a state-shaped save and neither can be recovered: the roster
        // is partial, so writing it deletes the absent; and a diff cannot say who did it, which every
        // IGuildGrain method needs because every guild rule is about authority.
        var guild = bridge.Warmed(Ana, Founded())!;

        bridge.SaveGuild(guild);

        Assert.Equal(1, bridge.StateWrites);
        Assert.Equal(0, bridge.Pending);
    }

    [Fact]
    public void DrainingDoesNotRemoveAndSettlingDoes() {
        bridge.Warmed(Ana, Row((ana, 0)));
        bridge.Invite(Ana, new(TheVigil), Bo);

        var edit = Assert.Single(bridge.Drain());

        Assert.Single(bridge.Drain());
        Assert.True(bridge.Settle(edit));
        Assert.Equal(0, bridge.Pending);
    }

    [Fact]
    public void AnEditTheGrainRefusedIsCountedAndRaisedAndNotRolledBack() {
        // ⚠ Undoing a join two frames later is a player who was in the guild, saw the roster, said
        // hello and was silently ejected. The next warm corrects it from the authority anyway.
        var raised = 0;

        bridge.Refused += _ => raised++;
        bridge.Warmed(Ana, Row((ana, 0)));
        bridge.Invite(Ana, new(TheVigil), Bo);

        Assert.True(bridge.Settle(bridge.Drain()[0], GuildRefusal.Full));
        Assert.Equal(1, bridge.Divergences);
        Assert.Equal(1, raised);
        Assert.Equal(1, bridge.LoadGuild(new(TheVigil))!.RankOf(Bo));
    }

    // ── Leaving ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SomebodyWhoLeavesIsUnseatedAndTheGuildStaysForEverybodyElse() {
        // ⚠ A guild is not one player's, and dropping it when one member logs out would make the
        // next member's chat go cold.
        bridge.Warmed(Ana, Founded());
        bridge.Warmed(Bo, Founded());

        Assert.True(bridge.Forget(Bo));
        Assert.NotNull(bridge.LoadGuild(new(TheVigil)));
        Assert.Equal(1, bridge.LoadGuild(new(TheVigil))!.Count);
    }

    [Fact]
    public void TheLastMemberLeavingDropsTheGuildFromTheView() {
        bridge.Warmed(Ana, Row((ana, 0)));

        bridge.Forget(Ana);

        Assert.Null(bridge.LoadGuild(new(TheVigil)));
    }

    [Fact]
    public void LeavingKeepsThePendingWrites() {
        bridge.Warmed(Ana, Row((ana, 0)));
        bridge.Invite(Ana, new(TheVigil), Bo);

        bridge.Forget(Ana);

        Assert.Equal(1, bridge.Pending);
    }

    // ── Graphs ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyTheNameablePartOfAFriendsListIsProjected() {
        var graph = bridge.Warmed(ana, [new(bo, SocialTie.Friend), new(cass, SocialTie.Friend)]);

        Assert.NotNull(graph);
        Assert.Equal([Bo], graph.Friends);
    }

    [Fact]
    public void AnOfflineFriendSurvivesASaveFromAPartialView() {
        // ⚠ Most of a friends list is offline. A graph that held only the online part would lose the
        // rest the first time it was written down.
        bridge.Warmed(ana, [new(bo, SocialTie.Friend), new(cass, SocialTie.Friend)]);
        bridge.LoadGraph(Ana)!.Unfriend(Bo);
        bridge.SaveGraph(bridge.LoadGraph(Ana)!);

        var write = Assert.Single(bridge.DrainGraphs());

        Assert.Equal(ana, write.Owner);
        Assert.Equal([new SocialLink(cass, SocialTie.Friend)], write.Links);
    }

    [Fact]
    public void ABlockOnSomebodyOfflineIsSeatedTheMomentTheyArrive() {
        // ⚠ What stops a block leaking. Blocked-while-offline is a PlayerKey in a durable set and
        // nothing in any graph, so IsSevered answers false and they can whisper, invite and trade —
        // every avenue the block was for.
        bridge.Warmed(ana, [new(cass, SocialTie.Blocked)]);

        var cassie = identity.Admit(cass, new NetworkPlayerId(3));

        Assert.False(bridge.Graphs.IsSevered(Ana, cassie));
        Assert.Equal(1, bridge.Admitted(cass, cassie));
        Assert.True(bridge.Graphs.IsSevered(Ana, cassie));
    }

    [Fact]
    public void AGraphWriteIsOneWritePerOwnerAndTheLastIsTheTruth() {
        bridge.Warmed(ana, []);
        bridge.LoadGraph(Ana)!.Seat(Bo, SocialTie.Friend);
        bridge.SaveGraph(bridge.LoadGraph(Ana)!);
        bridge.LoadGraph(Ana)!.Seat(Bo, SocialTie.Blocked);
        bridge.SaveGraph(bridge.LoadGraph(Ana)!);

        var write = Assert.Single(bridge.DrainGraphs());

        Assert.Equal([new SocialLink(bo, SocialTie.Blocked)], write.Links);
    }

    [Fact]
    public void ADrainedGraphSettlesDespiteBeingAnImmutableArray() {
        // ⚠ The trap doc 27 § Slice two records: generated record equality compares an
        // ImmutableArray by reference, so without hand-written equality nothing ever settles.
        bridge.Warmed(ana, [new(bo, SocialTie.Friend)]);
        bridge.SaveGraph(bridge.LoadGraph(Ana)!);

        Assert.True(bridge.Settle(bridge.DrainGraphs()[0]));
        Assert.Equal(0, bridge.PendingGraphs);
    }

    [Fact]
    public void SavingAGraphForSomebodyThisRealmDoesNotKnowIsCounted() {
        bridge.SaveGraph(new(new PlayerId(999)));

        Assert.Equal(1, bridge.ColdReads);
        Assert.Equal(0, bridge.PendingGraphs);
    }
}
