// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Xunit;

namespace Vixen.Live.Persistence.Tests;

/// <summary>Every semantic doc 27 § Persistence names, asserted against the in-memory store.</summary>
public class LedgerTests : IAsyncLifetime {
    static readonly AssetId Gold = new("currency/gold");
    static readonly AssetId Sword = new("items/greatsword");
    static readonly DateTimeOffset Noon = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    readonly MemoryPersistence store = new() { Clock = () => Noon };

    PlayerKey alice;
    PlayerKey bob;

    public async ValueTask InitializeAsync() {
        alice = await Character("Alice");
        bob = await Character("Bob");
    }

    public async ValueTask DisposeAsync() {
        await store.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_balanced_intent_moves_both_ends() {
        var result = await Grant(alice, Gold, 100);

        Assert.Equal(LedgerVerdict.Applied, result.Verdict);
        Assert.True(result.Ok);
        Assert.Equal(100, await store.Ledger.BalanceAsync(LedgerAccount.Of(alice), Gold, TestContext.Current.CancellationToken));
        Assert.Equal(-100, await store.Ledger.BalanceAsync(LedgerAccount.Of(LedgerAccount.Loot), Gold, TestContext.Current.CancellationToken));
    }

    /// <summary>
    ///     The faucet's balance is how much of an asset has entered the economy. Negative is the
    ///     healthy state and doc 28's economy dashboard is the consumer.
    /// </summary>
    [Fact]
    public async Task A_world_account_may_go_negative_and_a_character_may_not() {
        await Grant(alice, Gold, 10);

        var overdraft = await store.Ledger.AppendAsync(
            LedgerIntent.Transfer(Key(alice, "spend", "1"), 1, Noon, Account(alice), Account(bob), Gold, 11),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(LedgerVerdict.Insufficient, overdraft.Verdict);
        Assert.False(overdraft.Ok);
        Assert.Equal(10, await store.Ledger.BalanceAsync(Account(alice), Gold, TestContext.Current.CancellationToken));
        Assert.Equal(0, await store.Ledger.BalanceAsync(Account(bob), Gold, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unbalanced_intent_is_refused_whole() {
        var lopsided = new LedgerIntent {
            Key = Key(alice, "bug", "1"),
            LeaseEpoch = 1,
            At = Noon,
            Movements = [new(Account(alice), Gold, 500)]
        };

        var result = await store.Ledger.AppendAsync(lopsided, TestContext.Current.CancellationToken);

        Assert.Equal(LedgerVerdict.Unbalanced, result.Verdict);
        Assert.Equal(0, store.JournalLength);
    }

    [Fact]
    public async Task A_duplicate_delivery_writes_nothing_the_second_time() {
        var intent = LedgerIntent.Transfer(
            Key(alice, "loot", "boss-42"),
            1,
            Noon,
            LedgerAccount.Of(LedgerAccount.Loot),
            Account(alice),
            Sword,
            1
        );

        var first = await store.Ledger.AppendAsync(intent, TestContext.Current.CancellationToken);
        var second = await store.Ledger.AppendAsync(intent, TestContext.Current.CancellationToken);
        var third = await store.Ledger.AppendAsync(intent with { Detail = "retried by a different realm" }, TestContext.Current.CancellationToken);

        Assert.Equal(LedgerVerdict.Applied, first.Verdict);
        Assert.Equal(LedgerVerdict.Replayed, second.Verdict);
        Assert.Equal(LedgerVerdict.Replayed, third.Verdict);
        Assert.Equal(first.Sequence, second.Sequence);
        Assert.True(second.Ok);
        Assert.Equal(1, await store.Ledger.BalanceAsync(Account(alice), Sword, TestContext.Current.CancellationToken));
        Assert.Equal(2, store.JournalLength);
    }

    /// <summary>
    ///     The case a replay-after-the-fact has to survive: the operation has been superseded in
    ///     every other sense, and re-answering it must still be free rather than re-checked.
    /// </summary>
    [Fact]
    public async Task A_replay_is_recognised_even_when_it_could_no_longer_be_afforded() {
        await Grant(alice, Gold, 50);

        var spend = LedgerIntent.Transfer(
            Key(alice, "vendor", "7"),
            1,
            Noon,
            Account(alice),
            LedgerAccount.Of(LedgerAccount.Vendor),
            Gold,
            50
        );

        Assert.Equal(LedgerVerdict.Applied, (await store.Ledger.AppendAsync(spend, TestContext.Current.CancellationToken)).Verdict);
        Assert.Equal(0, await store.Ledger.BalanceAsync(Account(alice), Gold, TestContext.Current.CancellationToken));

        // Alice cannot afford it now. The retry must still be a no-op rather than Insufficient,
        // because the caller cannot tell whether the first attempt reached the store.
        Assert.Equal(LedgerVerdict.Replayed, (await store.Ledger.AppendAsync(spend, TestContext.Current.CancellationToken)).Verdict);
        Assert.Equal(0, await store.Ledger.BalanceAsync(Account(alice), Gold, TestContext.Current.CancellationToken));
    }

    /// <summary>ADR-021's late write, from the ledger's side.</summary>
    [Fact]
    public async Task An_intent_below_the_fence_is_superseded() {
        await Grant(alice, Gold, 100);

        // A transfer moves Alice to another realm, which writes her row at epoch 5.
        var record = await store.Players.ReadAsync(alice, TestContext.Current.CancellationToken);

        Assert.NotNull(record);
        Assert.Equal(WriteOutcome.Written, await store.Players.WriteAsync(record with { LeaseEpoch = 5 }, TestContext.Current.CancellationToken));

        var late = await store.Ledger.AppendAsync(
            LedgerIntent.Transfer(Key(alice, "spend", "late"), 1, Noon, Account(alice), Account(bob), Gold, 10),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(LedgerVerdict.Superseded, late.Verdict);
        Assert.Contains("fence", late.Detail, StringComparison.Ordinal);
        Assert.Equal(100, await store.Ledger.BalanceAsync(Account(alice), Gold, TestContext.Current.CancellationToken));
    }

    /// <summary>
    ///     ⚠ An accepted append raises the fence, exactly as an accepted write does. Without it a
    ///     realm could move value at epoch 5 and a <em>later</em> write at epoch 3 would still land,
    ///     because the fence would be wherever the last <c>WriteAsync</c> left it — a different and
    ///     weaker rule than the one everything else here is written against.
    /// </summary>
    [Fact]
    public async Task An_accepted_append_raises_the_fence_that_writes_are_checked_against() {
        var record = await store.Players.ReadAsync(alice, TestContext.Current.CancellationToken);

        Assert.NotNull(record);
        Assert.Equal(1, await store.Players.FenceAsync(alice, TestContext.Current.CancellationToken));

        var applied = await store.Ledger.AppendAsync(
            LedgerIntent.Transfer(
                Key(alice, "loot", "5"),
                5,
                Noon,
                LedgerAccount.Of(LedgerAccount.Loot),
                Account(alice),
                Gold,
                1
            ),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(LedgerVerdict.Applied, applied.Verdict);
        Assert.Equal(5, await store.Players.FenceAsync(alice, TestContext.Current.CancellationToken));

        Assert.Equal(
            WriteOutcome.Superseded,
            await store.Players.WriteAsync(record with { LeaseEpoch = 3 }, TestContext.Current.CancellationToken)
        );
    }

    /// <summary>A trade is one intent because a crash between its halves would be a lost sword.</summary>
    [Fact]
    public async Task A_trade_is_one_intent_with_four_movements() {
        await Grant(alice, Sword, 1);
        await Grant(bob, Gold, 500);

        var trade = new LedgerIntent {
            Key = Key(alice, "trade", "9c1"),
            LeaseEpoch = 1,
            At = Noon,
            Detail = "greatsword for 500g",
            Movements = [
                new(Account(alice), Sword, -1),
                new(Account(bob), Sword, 1),
                new(Account(bob), Gold, -500),
                new(Account(alice), Gold, 500)
            ]
        };

        Assert.True(trade.IsBalanced());
        Assert.Equal(LedgerVerdict.Applied, (await store.Ledger.AppendAsync(trade, TestContext.Current.CancellationToken)).Verdict);

        Assert.Equal(0, await store.Ledger.BalanceAsync(Account(alice), Sword, TestContext.Current.CancellationToken));
        Assert.Equal(1, await store.Ledger.BalanceAsync(Account(bob), Sword, TestContext.Current.CancellationToken));
        Assert.Equal(500, await store.Ledger.BalanceAsync(Account(alice), Gold, TestContext.Current.CancellationToken));
        Assert.Equal(0, await store.Ledger.BalanceAsync(Account(bob), Gold, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_trade_that_cannot_be_afforded_moves_nothing_at_all() {
        await Grant(alice, Sword, 1);
        await Grant(bob, Gold, 10);

        var trade = new LedgerIntent {
            Key = Key(alice, "trade", "9c2"),
            LeaseEpoch = 1,
            At = Noon,
            Movements = [
                new(Account(alice), Sword, -1),
                new(Account(bob), Sword, 1),
                new(Account(bob), Gold, -500),
                new(Account(alice), Gold, 500)
            ]
        };

        Assert.Equal(LedgerVerdict.Insufficient, (await store.Ledger.AppendAsync(trade, TestContext.Current.CancellationToken)).Verdict);
        Assert.Equal(1, await store.Ledger.BalanceAsync(Account(alice), Sword, TestContext.Current.CancellationToken));
        Assert.Equal(0, await store.Ledger.BalanceAsync(Account(bob), Sword, TestContext.Current.CancellationToken));
        Assert.Equal(10, await store.Ledger.BalanceAsync(Account(bob), Gold, TestContext.Current.CancellationToken));
        Assert.Empty(await store.Ledger.ReconcileAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Holdings_omit_the_zeroes() {
        await Grant(alice, Gold, 5);
        await Grant(alice, Sword, 1);

        await store.Ledger.AppendAsync(
            LedgerIntent.Transfer(
                Key(alice, "vendor", "8"),
                1,
                Noon,
                Account(alice),
                LedgerAccount.Of(LedgerAccount.Vendor),
                Sword,
                1
            ),
            TestContext.Current.CancellationToken
        );

        var holdings = await store.Ledger.HoldingsAsync(Account(alice), TestContext.Current.CancellationToken);

        Assert.Equal(5, holdings[Gold]);
        Assert.DoesNotContain(Sword, holdings.Keys);
    }

    /// <summary>Doc 27 § Diagnostics' support query: by player, by item, by operation.</summary>
    [Fact]
    public async Task History_narrows_and_comes_back_newest_first() {
        await Grant(alice, Gold, 100);
        await Grant(bob, Gold, 100);
        await Grant(alice, Sword, 1);

        var all = await store.Ledger.HistoryAsync(new(), TestContext.Current.CancellationToken);

        Assert.Equal(6, all.Count);
        Assert.True(all[0].Sequence > all[^1].Sequence);

        var hers = await store.Ledger.HistoryAsync(new() { Account = Account(alice) }, TestContext.Current.CancellationToken);

        Assert.Equal(2, hers.Count);
        Assert.All(hers, row => Assert.Equal(Account(alice), row.Account));

        var swords = await store.Ledger.HistoryAsync(new() { Asset = Sword }, TestContext.Current.CancellationToken);

        Assert.Equal(2, swords.Count);

        var one = await store.Ledger.HistoryAsync(new() { Operation = Key(alice, "grant", Sword.Address) }, TestContext.Current.CancellationToken);

        Assert.Equal(2, one.Count);

        var limited = await store.Ledger.HistoryAsync(new() { Limit = 1 }, TestContext.Current.CancellationToken);

        Assert.Single(limited);
        Assert.Equal(all[0].Sequence, limited[0].Sequence);
    }

    [Fact]
    public async Task A_row_records_both_clocks_and_the_balance_after_it() {
        var realmSaidAt = Noon.AddMinutes(-3);

        await store.Ledger.AppendAsync(
            LedgerIntent.Transfer(
                Key(alice, "loot", "1"),
                1,
                realmSaidAt,
                LedgerAccount.Of(LedgerAccount.Loot),
                Account(alice),
                Gold,
                7
            ),
            TestContext.Current.CancellationToken
        );

        var row = Assert.Single(await store.Ledger.HistoryAsync(new() { Account = Account(alice) }, TestContext.Current.CancellationToken));

        Assert.Equal(realmSaidAt, row.At);
        Assert.Equal(Noon, row.Recorded);
        Assert.Equal(7, row.Delta);
        Assert.Equal(7, row.Balance);
    }

    [Fact]
    public async Task Sequences_are_dense_and_monotonic_across_intents() {
        await Grant(alice, Gold, 1);
        await Grant(bob, Gold, 1);

        var rows = (await store.Ledger.HistoryAsync(new(), TestContext.Current.CancellationToken)).Reverse().ToImmutableArray();

        Assert.Equal([1L, 2L, 3L, 4L], rows.Select(row => row.Sequence));
    }

    [Fact]
    public async Task A_fresh_store_reconciles() {
        await Grant(alice, Gold, 100);
        await Grant(bob, Sword, 3);

        Assert.Empty(await store.Ledger.ReconcileAsync(TestContext.Current.CancellationToken));
    }

    async Task<PlayerKey> Character(string name) {
        var account = Guid.NewGuid();
        var key = new PlayerKey(account, Guid.NewGuid());

        await store.Players.CreateAsync(
            new(key, name, Noon, Noon, "eu", "maps/queensdale", 1, ReadOnlyMemory<byte>.Empty),
            TestContext.Current.CancellationToken
        );

        return key;
    }

    Task<LedgerResult> Grant(PlayerKey who, AssetId what, long many) =>
        store.Ledger.AppendAsync(
            LedgerIntent.Transfer(
                Key(who, "grant", what.Address),
                1,
                Noon,
                LedgerAccount.Of(LedgerAccount.Loot),
                LedgerAccount.Of(who),
                what,
                many
            ),
            TestContext.Current.CancellationToken
        );

    static LedgerAccount Account(PlayerKey who) => LedgerAccount.Of(who);

    static IdempotencyKey Key(PlayerKey who, string kind, string operation) => new(who, kind, operation);
}
