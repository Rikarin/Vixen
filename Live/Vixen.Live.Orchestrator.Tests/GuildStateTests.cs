// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Live.Cluster;
using Xunit;

namespace Vixen.Live.Orchestration.Tests;

/// <summary>
///     The grain doc 27 left undeclared at L1 because "declaring an interface nobody implements is a
///     promise rather than a contract" — G4 built the feature, so this is the contract.
/// </summary>
public sealed class GuildStateTests {
    static readonly PlayerKey Leader = new(Guid.NewGuid(), Guid.NewGuid());
    static readonly PlayerKey Officer = new(Guid.NewGuid(), Guid.NewGuid());
    static readonly PlayerKey Member = new(Guid.NewGuid(), Guid.NewGuid());
    static readonly PlayerKey Stranger = new(Guid.NewGuid(), Guid.NewGuid());

    DateTimeOffset clock = DateTimeOffset.UnixEpoch;

    readonly GuildState guild;

    public GuildStateTests() {
        guild = new(() => clock += TimeSpan.FromSeconds(1));
        guild.Found(Leader, "guilds/charter", "The Fellowship", 5);
        guild.Add(Leader, Officer, 1);
        guild.Add(Officer, Member, 2);
    }

    [Fact]
    public void FoundingMakesTheFounderTheLeader() {
        Assert.True(guild.Exists);
        Assert.Equal(0, guild.RankOf(Leader));
        Assert.Equal("The Fellowship", guild.Read().Name);
    }

    [Fact]
    public void AGuildIsFoundedOnce() =>
        Assert.Equal(GuildWrite.Founded, guild.Found(Officer, "guilds/other", "Again", 5).Write);

    [Fact]
    public void TheRecordCarriesTheCharterAddressRatherThanItsRanks() {
        // ⚠ A charter is content. A guild that stored its ranks would keep last patch's rules after
        // the patch; what is genuinely per-guild is the names a leader typed over them.
        Assert.Equal("guilds/charter", guild.Read().Charter);
        Assert.Empty(guild.Read().RankNames);
    }

    // ── Rank is the only thing the grain checks ───────────────────────────────────────────────

    [Fact]
    public void NobodyMayInviteAtOrAboveTheirOwnRank() {
        // ⚠ Otherwise an officer invites somebody as leader and the guild has two, or as their own
        // equal and can no longer remove them.
        Assert.Equal(GuildWrite.Outranked, guild.Add(Officer, Stranger, 1).Write);
        Assert.Equal(GuildWrite.Outranked, guild.Add(Officer, Stranger, 0).Write);
        Assert.Equal(GuildWrite.Applied, guild.Add(Officer, Stranger, 2).Write);
    }

    [Fact]
    public void TwoOfficersCannotDemoteEachOther() {
        // ⚠ The race no local check can win, which is exactly why the grain re-checks this and only
        // this. Both are rank 1.
        var second = new PlayerKey(Guid.NewGuid(), Guid.NewGuid());

        guild.Add(Leader, second, 1);

        Assert.Equal(GuildWrite.Outranked, guild.SetRank(Officer, second, 3).Write);
        Assert.Equal(GuildWrite.Outranked, guild.SetRank(second, Officer, 3).Write);
    }

    [Fact]
    public void NobodyMayPromoteAboveThemselves() {
        Assert.Equal(GuildWrite.Outranked, guild.SetRank(Officer, Member, 0).Write);
        Assert.Equal(GuildWrite.Outranked, guild.SetRank(Officer, Member, 1).Write);
        Assert.Equal(GuildWrite.Applied, guild.SetRank(Officer, Member, 3).Write);
    }

    [Fact]
    public void HandingTheGuildOverStepsTheOldLeaderDown() {
        // ⚠ Rank 0 is single. Two leaders is a state nothing in this interface could resolve.
        Assert.Equal(GuildWrite.Applied, guild.SetRank(Leader, Officer, 0).Write);

        Assert.Equal(0, guild.RankOf(Officer));
        Assert.Equal(1, guild.RankOf(Leader));
    }

    [Fact]
    public void ANonMemberIsNeitherAnActorNorATarget() {
        Assert.Equal(GuildWrite.NotAMember, guild.Add(Stranger, Officer, 3).Write);
        Assert.Equal(GuildWrite.NoSuchMember, guild.SetRank(Leader, Stranger, 2).Write);
        Assert.Equal(GuildWrite.NoSuchMember, guild.Remove(Leader, Stranger).Write);
    }

    // ── Leaving and removing ──────────────────────────────────────────────────────────────────

    [Fact]
    public void AnybodyMayLeaveButOnlySomebodyAboveMayRemove() {
        // Leaving is unconditional; removing somebody else has to outrank them.
        Assert.Equal(GuildWrite.Outranked, guild.Remove(Member, Officer).Write);
        Assert.Equal(GuildWrite.Applied, guild.Remove(Member, Member).Write);
    }

    [Fact]
    public void AnOfficerMayRemoveAMemberAndNotAPeer() {
        var second = new PlayerKey(Guid.NewGuid(), Guid.NewGuid());

        guild.Add(Leader, second, 1);

        Assert.Equal(GuildWrite.Outranked, guild.Remove(Officer, second).Write);
        Assert.Equal(GuildWrite.Applied, guild.Remove(Officer, Member).Write);
    }

    [Fact]
    public void TheLastLeaderMayOnlyLeaveWhenTheyAreTheLastMember() {
        // ⚠ A guild with no leader is one nobody can administer again, and nothing here could put
        // one back. Emptying it is how a guild ends.
        Assert.Equal(GuildWrite.Outranked, guild.Remove(Leader, Leader).Write);

        guild.Remove(Officer, Member);
        guild.Remove(Leader, Officer);

        Assert.Equal(GuildWrite.Applied, guild.Remove(Leader, Leader).Write);
        Assert.Equal(0, guild.Count);
    }

    // ── Capacity ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AFullGuildRefusesTheNextInvite() {
        for (var index = 0; index < 2; index++) {
            Assert.Equal(GuildWrite.Applied, guild.Add(Leader, new(Guid.NewGuid(), Guid.NewGuid()), 2).Write);
        }

        Assert.Equal(5, guild.Count);
        Assert.Equal(GuildWrite.Full, guild.Add(Leader, Stranger, 2).Write);
    }

    [Fact]
    public void ARosterOverTodaysCapacityLoadsAnywayAndStopsGrowing() {
        // ⚠ A charter that shrank must not evict people. What it does is stop new invites, which
        // falls out of Add's check without anything having to decide who goes.
        var saved = guild.Read();
        var loaded = new GuildState();

        loaded.Restore(saved, capacity: 1);

        Assert.Equal(3, loaded.Count);
        Assert.Equal(GuildWrite.Full, loaded.Add(Leader, Stranger, 2).Write);
    }

    // ── No-ops, revisions and names ───────────────────────────────────────────────────────────

    [Fact]
    public void ARetriedInviteIsUnchangedRatherThanRefused() {
        var before = guild.Revision;

        Assert.Equal(GuildWrite.Unchanged, guild.Add(Officer, Member, 2).Write);
        Assert.Equal(before, guild.Revision);
    }

    [Fact]
    public void EveryChangeMovesTheRevisionAndARefusalDoesNot() {
        var before = guild.Revision;

        Assert.Equal(GuildWrite.Outranked, guild.SetRank(Member, Officer, 3).Write);
        Assert.Equal(before, guild.Revision);

        Assert.Equal(before + 1, guild.SetRank(Leader, Member, 3).Revision);
    }

    [Fact]
    public void OnlyTheLeaderRenamesARankAndAnEmptyNamePutsTheCharterBack() {
        Assert.Equal(GuildWrite.Outranked, guild.RenameRank(Officer, 1, "Champion").Write);
        Assert.Equal(GuildWrite.Applied, guild.RenameRank(Leader, 1, "Champion").Write);
        Assert.Equal("Champion", guild.Read().RankNames[1]);

        Assert.Equal(GuildWrite.Applied, guild.RenameRank(Leader, 1, "").Write);
        Assert.Empty(guild.Read().RankNames);
    }

    [Fact]
    public void MembersComeBackInJoinOrder() =>
        Assert.Equal([Leader, Officer, Member], guild.Read().Members.Select(member => member.Player));

    [Fact]
    public void ASavedGuildComesBackAsItWas() {
        guild.RenameRank(Leader, 1, "Champion");

        var saved = guild.Read();
        var loaded = new GuildState();

        loaded.Restore(saved, capacity: 5);

        Assert.Equal(saved, loaded.Read());
    }

    [Fact]
    public void AGuildThatWasNeverFoundedAnswersNothingRatherThanThrowing() {
        var fresh = new GuildState();

        Assert.False(fresh.Read().Exists);
        Assert.Equal(GuildWrite.NotFound, fresh.Add(Leader, Officer, 1).Write);
        Assert.Equal(GuildWrite.NotFound, fresh.Remove(Leader, Officer).Write);
        Assert.Equal(GuildWrite.NotFound, fresh.RenameRank(Leader, 1, "x").Write);
    }
}
