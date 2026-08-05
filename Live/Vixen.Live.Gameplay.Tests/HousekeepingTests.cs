// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;
using Vixen.Gameplay.Economy;
using Vixen.Gameplay.Social;
using Xunit;
using NetworkPlayerId = Vixen.Net.Sessions.PlayerId;

namespace Vixen.Live.Gameplay.Tests;

/// <summary>
///     What a realm has to take away again, and what happens to a horizon that is set too short.
/// </summary>
/// <remarks>
///     ⚠ <b>Every one of these was found by a soak's memory line rather than by a failing
///     assertion</b>, which is the general lesson: a leak in a realm is not a bug that throws, it is a
///     number nobody was watching. <c>Samples/14-Mmo</c> went from 168 MB of growth over thirty
///     minutes to 13 MB on the strength of the three below.
/// </remarks>
public class HousekeepingTests {
    static readonly DateTimeOffset Noon = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    static readonly DefId Gold = DefId.From("currency/gold");

    readonly PlayerKey ana = new(Guid.NewGuid(), Guid.NewGuid());
    readonly PlayerKey ben = new(Guid.NewGuid(), Guid.NewGuid());
    readonly GameplayIdentityMap identity = new();

    static EconomyAccount Purse(PlayerId player) => new(player, string.Empty);

    static EconomyAccount World(string name) => new(PlayerId.None, name);

    // ── The horizon, and the outbox that makes it survivable ──────────────────────────────────

    /// <summary>
    ///     ⚠ The one that matters. A projection that has forgotten a key while its write is still
    ///     unsettled would otherwise apply the movements a second time — and the balances would be
    ///     wrong on a realm whose database is right, which is the hardest kind of wrong to find.
    /// </summary>
    [Fact]
    public void AKeyForgottenWhileItsWriteIsStillInFlightIsCaughtByTheOutbox() {
        var horizon = KeyHorizon.Outliving(TimeSpan.FromSeconds(1));
        var projection = new MemoryEconomyLedger(horizon);
        var bridge = new LedgerBridge(identity, projection, leaseEpoch: 1);
        var player = identity.Admit(ana, new NetworkPlayerId(1));

        bridge.Restore(Purse(player), Gold, 100);
        projection.Forget(Noon);

        Assert.Equal(EconomyVerdict.Applied, Spend(bridge, player).Verdict);

        // Nothing settles it, so the write is still in the outbox when the key ages out.
        for (var at = Noon; at <= Noon + horizon.Length + horizon.Interval; at += horizon.Interval) {
            projection.Forget(at);
        }

        Assert.Equal(0, projection.Keys);
        Assert.Equal(EconomyVerdict.Replayed, Spend(bridge, player).Verdict);
        Assert.Equal(1, bridge.Deduplicated);
        Assert.Equal(1, bridge.Pending);

        // The money moved once, which is the whole point of the guard.
        Assert.Equal(90, bridge.Balance(Purse(player), Gold));
    }

    /// <summary>
    ///     ⚠ The outbox answers even when the projection would have, and that is the order rather than
    ///     an accident: it is the record that cannot be wrong, so it is the one asked first. The cost
    ///     is that the counter says "a retry arrived before the write settled" rather than "the horizon
    ///     is too short" — two things worth knowing that this cannot tell apart.
    /// </summary>
    [Fact]
    public void AnUnsettledOperationIsRecognisedByTheOutboxEvenWhenTheProjectionRemembersIt() {
        var projection = new MemoryEconomyLedger();
        var bridge = new LedgerBridge(identity, projection, leaseEpoch: 1);
        var player = identity.Admit(ana, new NetworkPlayerId(1));

        bridge.Restore(Purse(player), Gold, 100);

        Assert.Equal(EconomyVerdict.Applied, Spend(bridge, player).Verdict);
        Assert.Equal(EconomyVerdict.Replayed, Spend(bridge, player).Verdict);
        Assert.Equal(1, bridge.Deduplicated);
        Assert.Equal(1, bridge.Pending);
        Assert.Equal(90, bridge.Balance(Purse(player), Gold));
    }

    /// <summary>⚠ Once the write is durable the outbox has let go, and only the horizon is left.</summary>
    [Fact]
    public void TheOutboxStopsGuardingOnceTheWriteHasSettled() {
        var projection = new MemoryEconomyLedger();
        var bridge = new LedgerBridge(identity, projection, leaseEpoch: 1);
        var player = identity.Admit(ana, new NetworkPlayerId(1));

        bridge.Restore(Purse(player), Gold, 100);
        Spend(bridge, player);

        foreach (var write in bridge.Drain()) {
            bridge.Settle(write.Key, new(Persistence.LedgerVerdict.Applied, 1));
        }

        Assert.Equal(0, bridge.Pending);
        Assert.Equal(EconomyVerdict.Replayed, Spend(bridge, player).Verdict);
        Assert.Equal(0, bridge.Deduplicated);
    }

    // ── The purse a departing player used to leave behind ──────────────────────────────────────

    /// <summary>
    ///     ⚠ The mirror of <c>Restore</c>. <c>Restore(…, 0)</c> looks like it undoes a seed and does
    ///     nothing at all, which is how five hundred travelling players left a row each on every shard
    ///     they had ever visited.
    /// </summary>
    [Fact]
    public void ADepartedPlayerLeavesNoRowBehind() {
        var projection = new MemoryEconomyLedger();
        var bridge = new LedgerBridge(identity, projection, leaseEpoch: 1);
        var player = identity.Admit(ana, new NetworkPlayerId(1));

        bridge.Restore(Purse(player), Gold, 250);

        Assert.False(bridge.Restore(Purse(player), Gold, 0));
        Assert.Equal(250, projection.Balance(Purse(player), Gold));

        projection.Release(Purse(player), World(LedgerBridge.RestoreAccount));

        Assert.Empty(projection.Holdings(Purse(player)));
        Assert.Equal(0, projection.Total(Gold));
    }

    // ── The graph a departing player used to leave behind ──────────────────────────────────────

    /// <summary>
    ///     ⚠ A hundred and thirty megabytes of the soak's growth, and it was one call away: the
    ///     bridge warms a graph on admission and nothing ever took one away.
    /// </summary>
    [Fact]
    public void ADepartedPlayerLeavesNoGraphBehind() {
        var bridge = new SocialBridge(identity);
        var graphs = bridge.Graphs;
        var player = identity.Admit(ana, new NetworkPlayerId(1));

        bridge.Warmed(ana, []);

        Assert.Equal(1, graphs.Count);

        bridge.Forget(player);

        Assert.Equal(0, graphs.Count);
    }

    /// <summary>
    ///     ⚠ The mirror of <c>Admitted</c>, and the half that is only findable from the durable set. A
    ///     gameplay id is never issued twice, so a tie left pointing at a departed one is never
    ///     replaced — the same player comes back as a different number and is seated beside their own
    ///     ghost.
    /// </summary>
    [Fact]
    public void ADepartedPlayerIsUnseatedFromEverybodyElsesGraph() {
        var bridge = new SocialBridge(identity);
        var graphs = bridge.Graphs;
        var left = identity.Admit(ana, new NetworkPlayerId(1));
        var right = identity.Admit(ben, new NetworkPlayerId(2));

        bridge.Warmed(ana, [new(ben, SocialTie.Friend)]);
        bridge.Warmed(ben, [new(ana, SocialTie.Friend)]);

        Assert.Contains(right, graphs.Of(left).Friends);
        Assert.Contains(left, graphs.Of(right).Friends);

        bridge.Forget(right);

        Assert.Empty(graphs.Of(left).Friends);
    }

    /// <summary>⚠ And the tie comes back on the id they come back as, rather than on the old one.</summary>
    [Fact]
    public void ComingBackSeatsTheNewIdAndNotTheOld() {
        var bridge = new SocialBridge(identity);
        var graphs = bridge.Graphs;
        var left = identity.Admit(ana, new NetworkPlayerId(1));
        var right = identity.Admit(ben, new NetworkPlayerId(2));

        bridge.Warmed(ana, [new(ben, SocialTie.Friend)]);
        bridge.Forget(right);
        identity.Release(right);

        var again = identity.Admit(ben, new NetworkPlayerId(3));

        bridge.Admitted(ben, again);

        Assert.Equal([again], graphs.Of(left).Friends);
    }

    static EconomyResult Spend(LedgerBridge bridge, PlayerId player) =>
        bridge.Post(
            EconomyIntent.Transfer("purchase:1", Purse(player), World(EconomyAccount.Vendor), Gold, 10)
        );
}
