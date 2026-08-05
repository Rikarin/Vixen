// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;
using Vixen.Gameplay.Economy;
using Vixen.Live.Persistence;
using Xunit;
using NetworkPlayerId = Vixen.Net.Sessions.PlayerId;

namespace Vixen.Live.Gameplay.Tests;

public class LedgerBridgeTests {
    static readonly DefId Gold = DefId.From("currency/gold");
    static readonly DefId Sword = DefId.From("items/sword");

    readonly PlayerKey ana = new(Guid.NewGuid(), Guid.NewGuid());
    readonly PlayerKey ben = new(Guid.NewGuid(), Guid.NewGuid());
    readonly GameplayIdentityMap identity = new();
    readonly MemoryEconomyLedger projection = new();
    readonly LedgerBridge bridge;

    PlayerId Ana { get; }

    PlayerId Ben { get; }

    public LedgerBridgeTests() {
        Ana = identity.Admit(ana, new NetworkPlayerId(1));
        Ben = identity.Admit(ben, new NetworkPlayerId(2));
        bridge = new(identity, projection, leaseEpoch: 7);
    }

    static EconomyAccount Purse(PlayerId player) => new(player, string.Empty);

    static EconomyAccount World(string name) => new(PlayerId.None, name);

    EconomyResult Give(PlayerId player, DefId asset, long amount, string key) =>
        bridge.Post(EconomyIntent.Transfer(key, World(EconomyAccount.Vendor), Purse(player), asset, amount));

    // ── Identity ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AGameplayIdIsItsSessionIdWidenedRatherThanAHash() {
        // ⚠ A hash of a 256-bit PlayerKey into 64 bits collides, and two players who collided would
        // write each other's inventory. The map is a table; nothing is derived.
        Assert.Equal(new PlayerId(1), Ana);
        Assert.Equal(new PlayerId(2), Ben);
        Assert.Equal(PlayerId.None, GameplayIdentityMap.From(new NetworkPlayerId(0)));
    }

    [Fact]
    public void TheThreeIdentitiesResolveBothWays() {
        Assert.True(identity.TryResolve(Ana, out var key));
        Assert.Equal(ana, key);
        Assert.Equal(Ana, identity.PlayerFor(ana));
        Assert.Equal(PlayerId.None, identity.PlayerFor(new(Guid.NewGuid(), Guid.NewGuid())));
    }

    [Fact]
    public void AReconnectReplacesTheIdRatherThanKeepingBoth() {
        // ⚠ Leaving the old one resolvable lets a stale rule write to somebody who is not there.
        var again = identity.Admit(ana, new NetworkPlayerId(9));

        Assert.Equal(new PlayerId(9), again);
        Assert.False(identity.TryResolve(Ana, out _));
        Assert.Equal(again, identity.PlayerFor(ana));
        Assert.Equal(2, identity.Count);
    }

    [Fact]
    public void ReleasingAnIdAReconnectHasAlreadyMovedDoesNotForgetThePlayer() {
        identity.Admit(ana, new NetworkPlayerId(9));

        Assert.False(identity.Release(Ana));
        Assert.Equal(new PlayerId(9), identity.PlayerFor(ana));
    }

    // ── Applied here, written down later ──────────────────────────────────────────────────────

    [Fact]
    public void AnIntentIsAppliedInTheFrameAndQueuedForTheDatabase() {
        Assert.True(Give(Ana, Gold, 100, "quest/errand").Ok);

        // Answered now, without a round trip. That is the whole reason the bridge exists.
        Assert.Equal(100, bridge.Balance(Purse(Ana), Gold));
        Assert.Equal(1, bridge.Pending);
    }

    [Fact]
    public void AQueuedWriteNamesTheLeaseEpochAndTheDurableIdentity() {
        Give(Ana, Sword, 1, "loot/skarr");

        var write = Assert.Single(bridge.Drain());

        Assert.Equal(7, write.Intent.LeaseEpoch);
        Assert.Equal(ana, write.Key.Player);
        Assert.Equal("loot/skarr", write.Key.Operation);

        // ⚠ Nothing carries a gameplay PlayerId. A realm-scoped integer in a durable row is somebody
        // else on the next realm.
        Assert.All(write.Intent.Movements, movement => Assert.True(movement.Account.Player == ana || movement.Account.Player == PlayerKey.None));
    }

    [Fact]
    public void DrainingDoesNotRemoveAnythingBecauseInFlightIsNotDone() {
        // ⚠ Removing here loses the intent when the grain call fails, and losing a ledger intent is
        // losing an item.
        Give(Ana, Gold, 10, "one");

        Assert.Single(bridge.Drain());
        Assert.Single(bridge.Drain());
        Assert.Equal(1, bridge.Pending);
    }

    [Fact]
    public void SettlingAnAppliedOrReplayedWriteClearsIt() {
        Give(Ana, Gold, 10, "one");
        Give(Ben, Gold, 20, "two");

        var writes = bridge.Drain();

        Assert.True(bridge.Settle(writes[0].Key, new(LedgerVerdict.Applied, 1)));
        Assert.True(bridge.Settle(writes[1].Key, new(LedgerVerdict.Replayed, 2)));
        Assert.Equal(0, bridge.Pending);
    }

    [Fact]
    public void AReplayIsNotQueuedTwice() {
        Assert.Equal(EconomyVerdict.Applied, Give(Ana, Gold, 10, "one").Verdict);
        Assert.Equal(EconomyVerdict.Replayed, Give(Ana, Gold, 10, "one").Verdict);

        Assert.Equal(1, bridge.Pending);
        Assert.Equal(10, bridge.Balance(Purse(Ana), Gold));
    }

    [Fact]
    public void AnIntentTheProjectionRefusesIsNotQueued() {
        // The projection is what checks affordability, so the database never sees an overdraft.
        var result = bridge.Post(
            EconomyIntent.Transfer("trade/1", Purse(Ana), Purse(Ben), Gold, 500)
        );

        Assert.False(result.Ok);
        Assert.Equal(BridgeRefusal.Refused, bridge.LastRefusal);
        Assert.Equal(0, bridge.Pending);
    }

    [Fact]
    public void AnAccountNamingSomebodyWhoIsNotHereIsRefusedBeforeAnythingMoves() {
        var stranger = new PlayerId(999);

        Assert.False(Give(stranger, Gold, 10, "ghost").Ok);
        Assert.Equal(BridgeRefusal.Unknown, bridge.LastRefusal);
        Assert.Equal(0, bridge.Pending);
        Assert.Equal(0, bridge.Balance(Purse(stranger), Gold));
    }

    // ── The lease ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASupersededWriteIsKeptRatherThanUndone() {
        // ⚠ ADR-021: a realm that loses its lease "keeps simulating, buffers durable mutations as
        // ledger intents, and either flushes them when the lease returns or hands them to the new
        // holder". Rolling the projection back would take an item off somebody still holding it.
        Give(Ana, Sword, 1, "loot/skarr");

        var write = Assert.Single(bridge.Drain());

        bridge.Settle(write.Key, new(LedgerVerdict.Superseded, 0));

        Assert.Equal(1, bridge.Pending);
        Assert.False(bridge.HoldsLease);
        Assert.Equal(1, bridge.Balance(Purse(Ana), Sword));
    }

    [Fact]
    public void ARealmWithNoLeaseStartsNothingNew() {
        bridge.Supersede();

        Assert.False(Give(Ana, Gold, 10, "one").Ok);
        Assert.Equal(BridgeRefusal.Superseded, bridge.LastRefusal);
        Assert.Equal(0, bridge.Pending);
    }

    [Fact]
    public void TheLeaseComingBackRestampsEverythingWaiting() {
        // ⚠ A write naming the dead epoch is declined by the same fence that declined it the first
        // time, for ever.
        Give(Ana, Gold, 10, "one");
        bridge.Settle(bridge.Drain()[0].Key, new(LedgerVerdict.Superseded, 0));

        bridge.Renew(8);

        Assert.True(bridge.HoldsLease);
        Assert.Equal(8, Assert.Single(bridge.Drain()).Intent.LeaseEpoch);
    }

    // ── Divergence ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADatabaseRefusalTheProjectionHadAcceptedIsADefectAndIsSurfaced() {
        // ⚠ The projection checked balance and balance-to-zero before this was ever queued, so the
        // database disagreeing means the lease's single-writer property has been broken. Counted and
        // raised rather than swallowed, because losing an item invisibly is unreproducible.
        Give(Ana, Gold, 10, "one");

        var write = Assert.Single(bridge.Drain());
        var raised = 0;

        bridge.Diverged += (_, _) => raised++;
        bridge.Settle(write.Key, new(LedgerVerdict.Insufficient, 0, "no"));

        Assert.Equal(1, raised);
        Assert.Equal(1, bridge.Divergences);
        Assert.Equal(1, bridge.Pending);
    }

    [Fact]
    public void SettlingSomethingThatIsNotWaitingSaysSo() =>
        Assert.False(bridge.Settle(new(ana, "gameplay", "never-sent"), new(LedgerVerdict.Applied, 1)));

    // ── Restoring ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASavedBalanceIsSeededWithoutQueueingAWrite() {
        // ⚠ From the database's balances, never replayed from its journal — a replay re-runs every
        // intent since the account was made, which is slow and a second chance to get it wrong.
        Assert.True(bridge.Restore(Purse(Ana), Gold, 2500));

        Assert.Equal(2500, bridge.Balance(Purse(Ana), Gold));
        Assert.Equal(0, bridge.Pending);
    }

    [Fact]
    public void ARestoredPlayerCanImmediatelySpendWhatTheyHad() {
        bridge.Restore(Purse(Ana), Gold, 100);

        Assert.True(bridge.Post(EconomyIntent.Transfer("trade/1", Purse(Ana), Purse(Ben), Gold, 40)).Ok);
        Assert.Equal(60, bridge.Balance(Purse(Ana), Gold));
        Assert.Equal(40, bridge.Balance(Purse(Ben), Gold));
        Assert.Equal(1, bridge.Pending);
    }

    [Fact]
    public void RestoringNothingDoesNothing() {
        Assert.False(bridge.Restore(Purse(Ana), Gold, 0));
        Assert.Equal(0, bridge.Pending);
    }

    // ── The oracle ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryQueuedWriteBalancesToZeroPerAsset() {
        // Doc 27 § Persistence's invariant, checked at the boundary rather than only in the database:
        // if an intent leaves here unbalanced, the ledger refuses it and the item is lost in flight.
        var random = GameplayRandom.For(0x1ED6E12ul, 3);

        bridge.Restore(Purse(Ana), Gold, 100_000);
        bridge.Restore(Purse(Ben), Gold, 100_000);

        for (var step = 0; step < 500; step++) {
            var from = random.Chance(0.5f) ? Purse(Ana) : Purse(Ben);
            var to = from.Player == Ana ? Purse(Ben) : Purse(Ana);

            switch (random.NextInt(0, 3)) {
                case 0:
                    bridge.Post(EconomyIntent.Transfer($"trade/{step}", from, to, Gold, random.NextInt(1, 50)));

                    break;

                case 1:
                    bridge.Post(EconomyIntent.Transfer($"loot/{step}", World(EconomyAccount.Vendor), to, Sword, 1));

                    break;

                default:
                    bridge.Post(EconomyIntent.Transfer($"fee/{step}", from, World(EconomyAccount.Sink), Gold, random.NextInt(1, 20)));

                    break;
            }
        }

        var writes = bridge.Drain();

        Assert.True(writes.Length > 400, $"only {writes.Length} writes queued");

        foreach (var write in writes) {
            var sums = new Dictionary<AssetId, long>();

            foreach (var movement in write.Intent.Movements) {
                sums[movement.Asset] = sums.GetValueOrDefault(movement.Asset) + movement.Delta;
            }

            Assert.All(sums, sum => Assert.Equal(0, sum.Value));
        }
    }
}
