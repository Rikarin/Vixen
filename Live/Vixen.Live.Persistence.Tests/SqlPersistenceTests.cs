// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Npgsql;
using Xunit;

namespace Vixen.Live.Persistence.Tests;

/// <summary>The same semantics, against a real PostgreSQL.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Everything else in this project runs against <c>MemoryPersistence</c>, and that is
///         correct — the semantics are properties of the semantics.</b> What no in-memory store can
///         tell you is whether the SQL in <see cref="Schema" /> parses, whether
///         <c>on conflict do nothing</c> means what the idempotency claim assumes, whether the full
///         outer join in <c>ReconcileAsync</c> is valid, or whether serializable isolation actually
///         refuses the concurrent overdraft the design rests on. Those are properties of PostgreSQL.
///     </para>
///     <para>
///         ⚠ <b>Skipped rather than failed when there is no database.</b> A developer's laptop has no
///         Postgres and their push should not be red for it; the nightly leg sets
///         <c>VIXEN_POSTGRES</c> and these run there. A skip is visible in the results, so the leg
///         going quiet is something somebody can see.
///     </para>
///     <para>
///         ⚠ <b>The two implementations have diverged once already.</b> An accepted ledger append
///         must raise the fence, which was true in <c>MemoryPersistence</c> and silently false in
///         <c>SqlPersistence</c> until reading one against the other caught it. Nothing but this file
///         would have caught the second one.
///     </para>
/// </remarks>
public class SqlPersistenceTests {
    /// <summary>Where the nightly leg says the database is.</summary>
    const string Variable = "VIXEN_POSTGRES";

    static readonly AssetId Gold = new("currency/gold");
    static readonly AssetId Sword = new("items/greatsword");
    static readonly DateTimeOffset Noon = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The schema goes up, and going up twice is not an error.</summary>
    [Fact]
    public async Task Migrating_is_idempotent() {
        await using var store = await OpenAsync();

        Assert.Equal(Schema.Version, await store.MigrateAsync(TestContext.Current.CancellationToken));
        Assert.Equal(Schema.Version, await store.MigrateAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_account_and_a_character_round_trip() {
        await using var store = await OpenAsync();

        var handle = $"nightly:{Guid.NewGuid():N}";
        var (account, created) = await store.Accounts.EnsureAsync(handle, Noon, TestContext.Current.CancellationToken);

        Assert.True(created);
        Assert.False((await store.Accounts.EnsureAsync(handle, Noon, TestContext.Current.CancellationToken)).Created);

        var key = new PlayerKey(account.Id, Guid.NewGuid());
        var record = new PlayerRecord(key, $"n{Guid.NewGuid():N}", Noon, Noon, "eu", "maps/queensdale", 1, new byte[] { 1, 2, 3 });

        Assert.Equal(WriteOutcome.Written, await store.Players.CreateAsync(record, TestContext.Current.CancellationToken));

        var read = await store.Players.ReadAsync(key, TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Equal(record, read);
        Assert.Equal([record], await store.Players.ForAccountAsync(account.Id, TestContext.Current.CancellationToken));
    }

    /// <summary>ADR-021's fence, as the `where` clause it is written as.</summary>
    [Fact]
    public async Task The_fence_only_ever_rises() {
        await using var store = await OpenAsync();

        var (record, _) = await CharacterAsync(store);

        Assert.Equal(WriteOutcome.Written, await store.Players.WriteAsync(record with { LeaseEpoch = 4 }, TestContext.Current.CancellationToken));
        Assert.Equal(WriteOutcome.Superseded, await store.Players.WriteAsync(record with { LeaseEpoch = 3 }, TestContext.Current.CancellationToken));
        Assert.Equal(4, await store.Players.FenceAsync(record.Key, TestContext.Current.CancellationToken));

        // The same epoch again is a realm writing repeatedly under one lease.
        Assert.Equal(WriteOutcome.Written, await store.Players.WriteAsync(record with { LeaseEpoch = 4 }, TestContext.Current.CancellationToken));
    }

    /// <summary>The `on conflict do nothing` the whole idempotency claim rests on.</summary>
    [Fact]
    public async Task A_duplicate_delivery_writes_nothing_the_second_time() {
        await using var store = await OpenAsync();

        var (record, _) = await CharacterAsync(store);

        var intent = LedgerIntent.Transfer(
            new(record.Key, "loot", Guid.NewGuid().ToString("N")),
            1,
            Noon,
            LedgerAccount.Of(LedgerAccount.Loot),
            LedgerAccount.Of(record.Key),
            Sword,
            1
        );

        var first = await store.Ledger.AppendAsync(intent, TestContext.Current.CancellationToken);
        var second = await store.Ledger.AppendAsync(intent, TestContext.Current.CancellationToken);

        Assert.Equal(LedgerVerdict.Applied, first.Verdict);
        Assert.Equal(LedgerVerdict.Replayed, second.Verdict);
        Assert.Equal(first.Sequence, second.Sequence);
        Assert.Equal(1, await store.Ledger.BalanceAsync(LedgerAccount.Of(record.Key), Sword, TestContext.Current.CancellationToken));
    }

    /// <summary>
    ///     ⚠ The divergence that already happened once: an accepted append must raise the fence, or a
    ///     realm that moved value at epoch 5 would still accept a later write at epoch 3.
    /// </summary>
    [Fact]
    public async Task An_accepted_append_raises_the_fence() {
        await using var store = await OpenAsync();

        var (record, _) = await CharacterAsync(store);

        var applied = await store.Ledger.AppendAsync(
            LedgerIntent.Transfer(
                new(record.Key, "loot", Guid.NewGuid().ToString("N")),
                5,
                Noon,
                LedgerAccount.Of(LedgerAccount.Loot),
                LedgerAccount.Of(record.Key),
                Gold,
                10
            ),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(LedgerVerdict.Applied, applied.Verdict);
        Assert.Equal(5, await store.Players.FenceAsync(record.Key, TestContext.Current.CancellationToken));
        Assert.Equal(
            WriteOutcome.Superseded,
            await store.Players.WriteAsync(record with { LeaseEpoch = 3 }, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task An_overdraft_is_refused_and_moves_nothing() {
        await using var store = await OpenAsync();

        var (record, _) = await CharacterAsync(store);
        var who = LedgerAccount.Of(record.Key);

        await store.Ledger.AppendAsync(
            LedgerIntent.Transfer(
                new(record.Key, "loot", Guid.NewGuid().ToString("N")),
                1,
                Noon,
                LedgerAccount.Of(LedgerAccount.Loot),
                who,
                Gold,
                10
            ),
            TestContext.Current.CancellationToken
        );

        var overdraft = await store.Ledger.AppendAsync(
            LedgerIntent.Transfer(
                new(record.Key, "vendor", Guid.NewGuid().ToString("N")),
                1,
                Noon,
                who,
                LedgerAccount.Of(LedgerAccount.Vendor),
                Gold,
                11
            ),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(LedgerVerdict.Insufficient, overdraft.Verdict);
        Assert.Equal(10, await store.Ledger.BalanceAsync(who, Gold, TestContext.Current.CancellationToken));
    }

    /// <summary>The full outer join, which no in-memory store exercises at all.</summary>
    [Fact]
    public async Task Reconciling_agrees_with_the_journal() {
        await using var store = await OpenAsync();

        var (record, _) = await CharacterAsync(store);

        await store.Ledger.AppendAsync(
            LedgerIntent.Transfer(
                new(record.Key, "loot", Guid.NewGuid().ToString("N")),
                1,
                Noon,
                LedgerAccount.Of(LedgerAccount.Loot),
                LedgerAccount.Of(record.Key),
                Gold,
                42
            ),
            TestContext.Current.CancellationToken
        );

        Assert.Empty(await store.Ledger.ReconcileAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_support_query_narrows() {
        await using var store = await OpenAsync();

        var (record, _) = await CharacterAsync(store);
        var operation = new IdempotencyKey(record.Key, "loot", Guid.NewGuid().ToString("N"));

        await store.Ledger.AppendAsync(
            LedgerIntent.Transfer(
                operation,
                1,
                Noon,
                LedgerAccount.Of(LedgerAccount.Loot),
                LedgerAccount.Of(record.Key),
                Sword,
                1
            ),
            TestContext.Current.CancellationToken
        );

        var byOperation = await store.Ledger.HistoryAsync(new() { Operation = operation }, TestContext.Current.CancellationToken);

        Assert.Equal(2, byOperation.Count);
        Assert.All(byOperation, row => Assert.Equal(operation, row.Key));

        var mine = await store.Ledger.HistoryAsync(
            new() { Account = LedgerAccount.Of(record.Key), Asset = Sword },
            TestContext.Current.CancellationToken
        );

        Assert.Single(mine);
    }

    /// <summary>
    ///     ⚠ The one that only a real database can answer: two concurrent spends of the same balance.
    ///     Under read-committed both read the old balance and both succeed, which is the overdraft the
    ///     design exists to make impossible — so this is the test that says the isolation level in
    ///     <c>AppendAsync</c> is doing what its comment claims.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_spends_cannot_both_win() {
        await using var store = await OpenAsync();

        var (record, _) = await CharacterAsync(store);
        var who = LedgerAccount.Of(record.Key);

        await store.Ledger.AppendAsync(
            LedgerIntent.Transfer(
                new(record.Key, "loot", Guid.NewGuid().ToString("N")),
                1,
                Noon,
                LedgerAccount.Of(LedgerAccount.Loot),
                who,
                Gold,
                100
            ),
            TestContext.Current.CancellationToken
        );

        var spends = Enumerable.Range(0, 2)
            .Select(index => Task.Run(async () => {
                    try {
                        return await store.Ledger.AppendAsync(
                            LedgerIntent.Transfer(
                                new(record.Key, "vendor", $"race-{index}"),
                                1,
                                Noon,
                                who,
                                LedgerAccount.Of(LedgerAccount.Vendor),
                                Gold,
                                100
                            ),
                            CancellationToken.None
                        );
                    } catch (NpgsqlException) {
                        // A serialization failure is the database refusing the second one, which is
                        // the correct outcome under a different name. Postgres surfaces it as an
                        // error rather than as a verdict, and a caller retries or gives up.
                        return new LedgerResult(LedgerVerdict.Superseded, 0, "serialization failure");
                    }
                }
            ));

        var results = await Task.WhenAll(spends);

        Assert.Single(results, result => result.Verdict == LedgerVerdict.Applied);
        Assert.True(await store.Ledger.BalanceAsync(who, Gold, TestContext.Current.CancellationToken) >= 0);
        Assert.Empty(await store.Ledger.ReconcileAsync(TestContext.Current.CancellationToken));
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Opens the nightly database, or skips this test.</summary>
    static async Task<SqlPersistence> OpenAsync() {
        var connection = Environment.GetEnvironmentVariable(Variable);

        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(connection),
            $"No {Variable} is set, so there is no PostgreSQL to check the SQL against. "
            + "The nightly leg sets it; a laptop is expected not to."
        );

        var store = new SqlPersistence(NpgsqlDataSource.Create(connection!), ownsSource: true);

        await store.MigrateAsync(CancellationToken.None);

        return store;
    }

    /// <summary>A character nobody else's test shares, because the database is not torn down.</summary>
    static async Task<(PlayerRecord Record, AccountRecord Account)> CharacterAsync(SqlPersistence store) {
        var (account, _) = await store.Accounts.EnsureAsync($"nightly:{Guid.NewGuid():N}", Noon, CancellationToken.None);

        var record = new PlayerRecord(
            new(account.Id, Guid.NewGuid()),
            $"n{Guid.NewGuid():N}",
            Noon,
            Noon,
            "eu",
            "maps/queensdale",
            1,
            ReadOnlyMemory<byte>.Empty
        );

        await store.Players.CreateAsync(record, CancellationToken.None);

        return (record, account);
    }
}
